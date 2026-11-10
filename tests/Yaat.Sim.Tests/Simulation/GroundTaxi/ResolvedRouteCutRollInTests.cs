using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A stand the graph reaches only the long way round is cut to across the apron, and the cut rolls in on the stand's
/// heading. SFO <c>TAXIAUTO @D1</c> from gate D3 (a plain <c>TAXI @D1</c> now stays inside the ramp): the graph route runs 1,612 ft out of the five alley and back, the cut
/// leaves the painted line on T5A and crosses to a point one fuselage out on D1's centreline, then rolls in on D1's
/// 345° heading — so the aircraft parks lined up with the stand, not at the angle the crossing arrived on.
/// </summary>
public class ResolvedRouteCutRollInTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far off the stand heading the parked aircraft may rest: the roll-in arc is fitted to the aircraft's live pose
    /// so the straight is flown lined up, where a straight cut from T5A once parked SKW3398 43° off D1's heading.
    /// </summary>
    private const double ParkedHeadingToleranceDeg = 1.0;

    /// <summary>How far past the stand heading, in the corner's own turn sense, the roll-in may swing before it settles on it.</summary>
    private const double MaxOvershootDeg = 1.0;

    /// <summary>How far outside the roll-in line, opposite the corner's turn sense, the roll-in arc may swing.</summary>
    private const double MaxWideFt = 2.0;

    /// <summary>Share of the roll-in's length the aircraft must already be within the parked tolerance of the stand heading.</summary>
    private const double MinLinedUpShare = 0.75;

    [Fact]
    public void TaxiToStandTheLongWayRound_CutsAcrossAndRollsInOnStandHeading()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("RampLaneReposition", LogLevel.Debug).InitializeSimLog();
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW3398", "E75L", "D3");
        // TAXIAUTO: the unrestricted auto-route runs the long way round over A, which a plain TAXI from the ramp never drives.
        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXIAUTO @D1");
        output.WriteLine($"TAXIAUTO @D1: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);

        GroundNode d1 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("D1"));
        double standHeading = Assert.IsType<TrueHeading>(d1.TrueHeading).Degrees;
        TaxiRouteSegment crossing = route.Segments[^2];
        TaxiRouteSegment rollIn = route.Segments[^1];
        Assert.True(VirtualNode.IsVirtualEdge(crossing.Edge.Edge), $"the second-last segment should be the apron crossing: {crossing.TaxiwayName}");
        Assert.True(VirtualNode.IsVirtualEdge(rollIn.Edge.Edge), $"the last segment should be the roll-in: {rollIn.TaxiwayName}");
        Assert.True(crossing.Edge.FromNode.Edges.Any(e => e.MatchesTaxiway("T5A")), $"the cut should leave T5A, not #{crossing.FromNodeId}");
        Assert.Equal(crossing.ToNodeId, rollIn.FromNodeId);
        Assert.Equal(d1.Id, rollIn.ToNodeId);
        Assert.True(
            GeoMath.AbsBearingDifference(rollIn.Edge.ArrivalBearing, standHeading) <= 1.0,
            $"the roll-in runs {rollIn.Edge.ArrivalBearing:F1}°, not on the {standHeading:F1}° stand heading"
        );
        double rollInFt = rollIn.Edge.DistanceNm * GeoMath.FeetPerNm;
        Assert.InRange(rollInFt, AircraftLength.ResolveFt("E75L") - 1.0, AircraftLength.ResolveFt("E75L") + 1.0);
        Assert.True(route.TotalDistanceFt < 1000.0, $"the cut route is {route.TotalDistanceFt:F0} ft; the graph's long way round is 1,612 ft");

        double turnSense = Math.Sign(GeoMath.SignedBearingDifference(crossing.Edge.ArrivalBearing, standHeading));
        var rollInLine = new TrueHeading(standHeading);
        LatLon approach = rollIn.Edge.FromNode.Position;
        double overshootDeg = double.MinValue;
        double wideFt = double.MinValue;
        double linedUpFromFt = double.NaN;
        int samples = 0;
        int rollInIndex = route.Segments.Count - 1;
        AircraftState? parked = SubTickDriver.TickSubTicksUntil(
            ground.Engine,
            "SKW3398",
            ac => ac.Phases?.CurrentPhase is AtParkingPhase,
            400,
            ac =>
            {
                if (route.CurrentSegmentIndex != rollInIndex)
                {
                    return;
                }

                samples++;
                double errorDeg = GeoMath.SignedBearingDifference(standHeading, ac.TrueHeading.Degrees);
                double offLineFt = GeoMath.SignedCrossTrackDistanceNm(ac.Position, approach, rollInLine) * GeoMath.FeetPerNm;
                double alongFt = GeoMath.AlongTrackDistanceNm(ac.Position, approach, rollInLine) * GeoMath.FeetPerNm;
                overshootDeg = Math.Max(overshootDeg, turnSense * errorDeg);
                wideFt = Math.Max(wideFt, -turnSense * offLineFt);
                if (Math.Abs(errorDeg) > ParkedHeadingToleranceDeg)
                {
                    linedUpFromFt = double.NaN;
                }
                else if (double.IsNaN(linedUpFromFt))
                {
                    linedUpFromFt = alongFt;
                }
            }
        );
        Assert.NotNull(parked);
        double parkedErrorDeg = GeoMath.AbsBearingDifference(parked.TrueHeading.Degrees, standHeading);
        double stopAlongFt = GeoMath.AlongTrackDistanceNm(parked.Position, approach, rollInLine) * GeoMath.FeetPerNm;
        double linedUpFt = double.IsNaN(linedUpFromFt) ? 0.0 : stopAlongFt - linedUpFromFt;
        output.WriteLine(
            $"SKW3398 at D1: heading {parked.TrueHeading.Degrees:F1}° (stand {standHeading:F1}°), "
                + $"{parkedErrorDeg:F2}° at the stop, overshoot {overshootDeg:F2}°, {wideFt:F2} ft wide, "
                + $"{linedUpFt:F1} of {rollInFt:F1} ft lined up, {samples} sub-tick samples"
        );
        Assert.True(samples > 0, "the roll-in segment was never sampled");
        Assert.True(
            parkedErrorDeg <= ParkedHeadingToleranceDeg,
            $"SKW3398 parked at {parked.TrueHeading.Degrees:F1}°, {parkedErrorDeg:F2}° off the stand heading"
        );
        Assert.True(overshootDeg <= MaxOvershootDeg, $"the roll-in overshot the stand heading by {overshootDeg:F2}°");
        Assert.True(wideFt <= MaxWideFt, $"the roll-in swung {wideFt:F2} ft outside the roll-in line");
        Assert.True(
            linedUpFt / rollInFt >= MinLinedUpShare,
            $"the roll-in was within {ParkedHeadingToleranceDeg:F1}° for only {linedUpFt:F1} of its {rollInFt:F1} ft"
        );
    }

    /// <summary>A plain <c>TAXI @D1</c> from D3 names no taxiway, so it stays inside the ramp: it cuts across the apron and never drives A.</summary>
    [Fact]
    public void PlainTaxiToD1_FromD3_StaysInsideTheRamp()
    {
        if (SfoGroundHarness.Build(output, autoCrossMode: AutoCrossRunwayMode.Off) is not { } ground)
        {
            return;
        }

        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW3398", "E75L", "D3");
        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI @D1");
        output.WriteLine($"TAXI @D1: {result.Success} — {result.Message}");
        Assert.True(result.Success, result.Message);
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        SfoGroundHarness.DumpRoute(output, route);

        GroundNode d1 = Assert.IsType<GroundNode>(ground.Layout.FindParkingByName("D1"));
        Assert.Equal(d1.Id, route.Segments[^1].ToNodeId);
        Assert.DoesNotContain(route.Segments, s => s.Edge.Edge.MatchesTaxiway("A"));
        Assert.Contains(route.Segments, s => VirtualNode.IsVirtualEdge(s.Edge.Edge) && (s.FromNodeId >= 0) && (s.ToNodeId >= 0));
    }
}
