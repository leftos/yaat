using Xunit;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests;

/// <summary>
/// Start-node selection for <see cref="LineUpGraphRoute.TryPlan"/> (issue #438). An aircraft holding short of
/// KSFO 01R at A1 stops a couple of feet past the taxiway node it just rolled over; that node is behind the
/// nose, and routing from it prepends a virtual approach segment pointing backward, which the navigator
/// answers with a near-180° entry-alignment turn at the tight-turn speed floor — the aircraft pivots a full
/// circle before lining up. The route must always start ahead of the nose: either on the node the aircraft is
/// standing on (no approach segment) or on a node it has yet to reach.
/// </summary>
[Collection("NavDbMutator")]
public class LineUpGraphRouteTests(ITestOutputHelper output)
{
    /// <summary>N346G stopped at node 835, the 28R hold-short bar on taxiway E, facing 228.4° down E.</summary>
    private static readonly LatLon HoldShortPose = new(37.622192, -122.375736);

    private const double HoldShortHeadingDeg = 228.4;

    /// <summary>UAL859 stopped 2.8 ft past the A1 taxiway node, holding short of 01R.</summary>
    [Fact]
    public void Ual859Pose_RouteStartsAheadOfTheNose()
    {
        if (!TryResolveInputs("01R", out RunwayInfo? runway, out AirportGroundLayout? layout))
        {
            return;
        }

        const double headingDeg = 120.85;
        LineUpArcFollowPlan? plan = LineUpGraphRoute.TryPlan(
            layout,
            new LatLon(37.60687155024075, -122.381946548138),
            new TrueHeading(headingDeg),
            runway,
            AircraftCategory.Jet
        );

        Assert.NotNull(plan);
        AssertFirstSegmentPointsForward(plan.Route, headingDeg);
        AssertStartsOnARealNode(plan.Route);
        AssertNoDegenerateSegments(plan.Route);
    }

    /// <summary>DAL819 stopped 2.6 ft past the M1 taxiway node, holding short of 01L.</summary>
    [Fact]
    public void Dal819Pose_RouteStartsAheadOfTheNose()
    {
        if (!TryResolveInputs("01L", out RunwayInfo? runway, out AirportGroundLayout? layout))
        {
            return;
        }

        const double headingDeg = 120.79;
        LineUpArcFollowPlan? plan = LineUpGraphRoute.TryPlan(
            layout,
            new LatLon(37.608439953128396, -122.38383474182635),
            new TrueHeading(headingDeg),
            runway,
            AircraftCategory.Jet
        );

        Assert.NotNull(plan);
        AssertFirstSegmentPointsForward(plan.Route, headingDeg);
        AssertStartsOnARealNode(plan.Route);
        AssertNoDegenerateSegments(plan.Route);
    }

    /// <summary>
    /// DAL2154 stopped 7.5 ft past the same A1 node — far enough that the node is rejected outright and the
    /// route starts from the hold-short node ahead over a forward virtual approach segment. This pose already
    /// worked; it pins that the start-node fix does not regress it.
    /// </summary>
    [Fact]
    public void Dal2154Pose_StillResolves()
    {
        if (!TryResolveInputs("01R", out RunwayInfo? runway, out AirportGroundLayout? layout))
        {
            return;
        }

        const double headingDeg = 120.85;
        LineUpArcFollowPlan? plan = LineUpGraphRoute.TryPlan(
            layout,
            new LatLon(37.60686528515113, -122.38193236056337),
            new TrueHeading(headingDeg),
            runway,
            AircraftCategory.Jet
        );

        Assert.NotNull(plan);
        AssertFirstSegmentPointsForward(plan.Route, headingDeg);
    }

    /// <summary>
    /// N346G holding short of KSFO 28R at taxiway E — a runway-CROSSING taxiway, so the approach side carries
    /// two tangent-cut nodes: the one whose fillet arc curves onto 28R and the one whose arc curves onto the
    /// 10L reciprocal. The walk toward the runway meets the 10L one first; it must not step through that arc
    /// onto the runway centerline and strand itself there, because the route it then fails to build is the
    /// only one that lines the aircraft up at taxi speed (the synthetic fallback crawls a 282 ft nose-out at
    /// the piston arc speed of 4.4 kt).
    /// </summary>
    [Fact]
    public void N346GPose_LiningUpOnto28R_TakesTheDepartureSideFilletArc()
    {
        if (!TryResolveInputs("28R", out RunwayInfo? runway, out AirportGroundLayout? layout))
        {
            return;
        }

        LineUpArcFollowPlan? plan = LineUpGraphRoute.TryPlan(
            layout,
            HoldShortPose,
            new TrueHeading(HoldShortHeadingDeg),
            runway,
            AircraftCategory.Piston
        );

        Assert.NotNull(plan);
        output.WriteLine(
            $"[28R] rwyHdg={plan.RunwayHeadingDeg:F1}° centerline node {plan.CenterlineNode.Id} "
                + $"at ({plan.CenterlineNode.Position.Lat:F6}, {plan.CenterlineNode.Position.Lon:F6}), {plan.Route.Segments.Count} segments"
        );

        Assert.Equal(297.9, plan.RunwayHeadingDeg, 1.0);

        double centerlineCrossFt = CrossFt(plan.CenterlineNode.Position, runway);
        Assert.True(centerlineCrossFt < 10.0, $"centerline node {plan.CenterlineNode.Id} is {centerlineCrossFt:F1}ft off the 28R centerline");

        // The departure-side node lies north-west of the E junction (farther down 28R from its threshold);
        // the 10L-side node the broken walk reached lies south-east of it.
        double centerlineAlongFt = AlongFt(plan.CenterlineNode.Position, runway);
        double junctionAlongFt = AlongFt(HoldShortPose, runway);
        output.WriteLine($"[28R] along-track from the 28R threshold: centerline node {centerlineAlongFt:F0}ft, hold-short {junctionAlongFt:F0}ft");
        Assert.True(
            centerlineAlongFt > junctionAlongFt,
            $"centerline node {plan.CenterlineNode.Id} is {junctionAlongFt - centerlineAlongFt:F0}ft south-east of the E junction — "
                + "that is the 10L-side prong, the wrong way down the runway"
        );

        TaxiRouteSegment arcSegment = plan.Route.Segments[^2];
        Assert.True(
            arcSegment.Edge.Edge is GroundArc,
            $"second-to-last segment is {arcSegment.Edge.Edge.GetType().Name} on {arcSegment.TaxiwayName}, not the junction fillet arc"
        );
        Assert.Contains("RWY", arcSegment.TaxiwayName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The same pose lining up onto 10L — the reciprocal end, reached over the other prong of the same
    /// crossing. This already resolved before the crossing-node fix; it pins that skipping centerline nodes
    /// in the walk does not cost the reciprocal case its route, and that
    /// <c>TangentAlignToleranceDeg</c> still picks the prong pointing down 10L.
    /// </summary>
    [Fact]
    public void N346GPose_LiningUpOnto10L_StillResolvesOnTheSouthEastProng()
    {
        if (
            !TryResolveInputs("10L", out RunwayInfo? runway, out AirportGroundLayout? layout)
            || !TryResolveInputs("28R", out RunwayInfo? runway28R, out _)
        )
        {
            return;
        }

        LineUpArcFollowPlan? plan = LineUpGraphRoute.TryPlan(
            layout,
            HoldShortPose,
            new TrueHeading(HoldShortHeadingDeg),
            runway,
            AircraftCategory.Piston
        );

        Assert.NotNull(plan);
        output.WriteLine(
            $"[10L] rwyHdg={plan.RunwayHeadingDeg:F1}° centerline node {plan.CenterlineNode.Id} "
                + $"at ({plan.CenterlineNode.Position.Lat:F6}, {plan.CenterlineNode.Position.Lon:F6}), {plan.Route.Segments.Count} segments"
        );

        Assert.Equal(117.9, plan.RunwayHeadingDeg, 1.0);

        double centerlineAlongFt = AlongFt(plan.CenterlineNode.Position, runway28R);
        double junctionAlongFt = AlongFt(HoldShortPose, runway28R);
        output.WriteLine($"[10L] along-track from the 28R threshold: centerline node {centerlineAlongFt:F0}ft, hold-short {junctionAlongFt:F0}ft");
        Assert.True(
            centerlineAlongFt < junctionAlongFt,
            $"centerline node {plan.CenterlineNode.Id} is {centerlineAlongFt - junctionAlongFt:F0}ft north-west of the E junction — "
                + "a 10L departure must line up on the south-east prong"
        );
    }

    /// <summary>Distance (ft) of a position from the runway centerline.</summary>
    private static double CrossFt(LatLon p, RunwayInfo runway) =>
        Math.Abs(GeoMath.SignedCrossTrackDistanceNm(p, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading))
        * GeoMath.FeetPerNm;

    /// <summary>Along-track distance (ft) of a position from the runway threshold, positive in the departure direction.</summary>
    private static double AlongFt(LatLon p, RunwayInfo runway) =>
        GeoMath.AlongTrackDistanceNm(p, new LatLon(runway.ThresholdLatitude, runway.ThresholdLongitude), runway.TrueHeading) * GeoMath.FeetPerNm;

    private void AssertFirstSegmentPointsForward(TaxiRoute route, double headingDeg)
    {
        TaxiRouteSegment first = route.Segments[0];
        double deltaDeg = GeoMath.AbsBearingDifference(first.Edge.DepartureBearing, headingDeg);
        output.WriteLine(
            $"[first segment] from={first.FromNodeId} to={first.ToNodeId} taxiway={first.TaxiwayName} "
                + $"bearing={first.Edge.DepartureBearing:F1}° delta={deltaDeg:F1}° len={first.Edge.DistanceNm * GeoMath.FeetPerNm:F1}ft"
        );

        Assert.True(
            deltaDeg < 45.0,
            $"first route segment departs on {first.Edge.DepartureBearing:F1}°, {deltaDeg:F1}° off the aircraft heading {headingDeg:F1}° — "
                + "the route starts behind the nose"
        );
    }

    private void AssertStartsOnARealNode(TaxiRoute route)
    {
        TaxiRouteSegment first = route.Segments[0];
        Assert.True(
            first.FromNodeId >= 0,
            $"route starts on virtual node {first.FromNodeId}: the aircraft is standing on a graph node, so the route must begin "
                + "on the real taxiway edge rather than over a virtual approach segment"
        );
    }

    private void AssertNoDegenerateSegments(TaxiRoute route)
    {
        for (int i = 0; i < route.Segments.Count; i++)
        {
            double lengthFt = route.Segments[i].Edge.DistanceNm * GeoMath.FeetPerNm;
            output.WriteLine(
                $"[segment {i}] {route.Segments[i].TaxiwayName} len={lengthFt:F1}ft bearing={route.Segments[i].Edge.DepartureBearing:F1}°"
            );
            Assert.True(lengthFt >= 5.0, $"segment {i} is {lengthFt:F2}ft long — too short to carry a usable bearing");
        }
    }

    private bool TryResolveInputs(string runwayDesignator, out RunwayInfo runway, out AirportGroundLayout layout)
    {
        runway = null!;
        layout = null!;

        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            output.WriteLine("SKIP: navdata not available");
            return false;
        }

        RunwayInfo? resolvedRunway = TestVnasData.NavigationDb.GetRunway("KSFO", runwayDesignator);
        if (resolvedRunway is null)
        {
            output.WriteLine($"SKIP: KSFO {runwayDesignator} not in navdata");
            return false;
        }

        AirportGroundLayout? resolvedLayout = new TestAirportGroundData().GetLayout("SFO");
        if (resolvedLayout is null)
        {
            output.WriteLine("SKIP: SFO ground layout not available");
            return false;
        }

        runway = resolvedRunway;
        layout = resolvedLayout;
        return true;
    }
}
