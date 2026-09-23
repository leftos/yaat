using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A stand the graph reaches only the long way round is cut to across the apron, and the cut rolls in on the stand's
/// heading. SFO <c>TAXI @D1</c> from gate D3: the graph route runs 1,612 ft out of the five alley and back, the cut
/// leaves the painted line on T5A and crosses to a point one fuselage out on D1's centreline, then rolls in on D1's
/// 345° heading — so the aircraft parks lined up with the stand, not at the angle the crossing arrived on.
/// </summary>
public class ResolvedRouteCutRollInTests(ITestOutputHelper output)
{
    /// <summary>
    /// How far off the stand heading the parked aircraft may rest. Measured, not published: this run parks 2.8° off
    /// (the aircraft leaves the gate on a short route and is still settling on the roll-in), where a straight cut from
    /// T5A parked SKW3398 43° off D1's heading in issue #454.
    /// </summary>
    private const double ParkedHeadingToleranceDeg = 5.0;

    [Fact]
    public void TaxiToStandTheLongWayRound_CutsAcrossAndRollsInOnStandHeading()
    {
        if (SfoGroundHarness.Build(output, autoCross: false) is not { } ground)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("RampLaneReposition", LogLevel.Debug).InitializeSimLog();
        AircraftState aircraft = SfoGroundHarness.SpawnParked(ground, "SKW3398", "E75L", "D3");
        CommandResult result = ground.Engine.SendCommand("SKW3398", "TAXI @D1");
        output.WriteLine($"TAXI @D1: {result.Success} — {result.Message}");
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
        Assert.InRange(rollInFt, TugMovePlanner.FuselageLengthFt("E75L") - 1.0, TugMovePlanner.FuselageLengthFt("E75L") + 1.0);
        Assert.True(route.TotalDistanceFt < 1000.0, $"the cut route is {route.TotalDistanceFt:F0} ft; the graph's long way round is 1,612 ft");

        int parkedAt = SfoGroundHarness.TickUntil(ground.Engine, () => aircraft.Phases?.CurrentPhase is AtParkingPhase, 400, null);
        double errorDeg = GeoMath.AbsBearingDifference(aircraft.TrueHeading.Degrees, standHeading);
        double offStandFt = GeoMath.DistanceNm(aircraft.Position, d1.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"SKW3398 at D1 after {parkedAt}s: heading {aircraft.TrueHeading.Degrees:F1}° (stand {standHeading:F1}°), {offStandFt:F0} ft off the stand"
        );
        Assert.True(parkedAt > 0, $"SKW3398 never parked at D1: {aircraft.Phases?.CurrentPhase?.Name}");
        Assert.True(
            errorDeg <= ParkedHeadingToleranceDeg,
            $"SKW3398 parked at {aircraft.TrueHeading.Degrees:F1}°, {errorDeg:F1}° off the stand heading"
        );
    }
}
