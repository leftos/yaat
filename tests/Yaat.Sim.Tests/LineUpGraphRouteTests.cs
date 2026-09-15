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
    /// <summary>UAL859 stopped 2.8 ft past the A1 taxiway node, holding short of 01R.</summary>
    [Fact]
    public void Ual859Pose_RouteStartsAheadOfTheNose()
    {
        if (!TryResolveInputs("01R", out var runway, out var layout))
        {
            return;
        }

        const double headingDeg = 120.85;
        var plan = LineUpGraphRoute.TryPlan(
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
        if (!TryResolveInputs("01L", out var runway, out var layout))
        {
            return;
        }

        const double headingDeg = 120.79;
        var plan = LineUpGraphRoute.TryPlan(
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
        if (!TryResolveInputs("01R", out var runway, out var layout))
        {
            return;
        }

        const double headingDeg = 120.85;
        var plan = LineUpGraphRoute.TryPlan(
            layout,
            new LatLon(37.60686528515113, -122.38193236056337),
            new TrueHeading(headingDeg),
            runway,
            AircraftCategory.Jet
        );

        Assert.NotNull(plan);
        AssertFirstSegmentPointsForward(plan.Route, headingDeg);
    }

    private void AssertFirstSegmentPointsForward(TaxiRoute route, double headingDeg)
    {
        var first = route.Segments[0];
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
        var first = route.Segments[0];
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

        var resolvedRunway = TestVnasData.NavigationDb.GetRunway("KSFO", runwayDesignator);
        if (resolvedRunway is null)
        {
            output.WriteLine($"SKIP: KSFO {runwayDesignator} not in navdata");
            return false;
        }

        var resolvedLayout = new TestAirportGroundData().GetLayout("SFO");
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
