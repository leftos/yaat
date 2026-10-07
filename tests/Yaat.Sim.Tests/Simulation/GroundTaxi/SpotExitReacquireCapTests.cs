using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The establish-straight gate's own job: an aircraft that turns out of a stand from rest has no incoming leg to round
/// tangent, so it finishes the turn off the outgoing centreline, and re-acquires the line at the slow re-acquire speed
/// (5 kt) rather than accelerating into a pure-pursuit overshoot. At KOAK a B738 taxiing out of SIG4 makes a ~109° turn
/// leaving the stand.
/// </summary>
public class SpotExitReacquireCapTests(ITestOutputHelper output)
{
    /// <summary>How far (kt) physics may read above the target speed it is settling onto.</summary>
    private const double PhysicsSettleKts = 0.01;

    private const int TickSeconds = 60;

    [Fact]
    public void B738OutOfSig4_OffTheOutgoingCentreline_HeldAtReacquireSpeed()
    {
        TestVnasData.EnsureInitialized();
        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var groundData = new TestAirportGroundData(FilletMode.Standard);
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return;
        }

        GroundNode stand = Assert.IsType<GroundNode>(layout.FindParkingByName("SIG4"));
        var engine = new SimulationEngine(groundData)
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-sig4-reacquire",
                ScenarioName = "OAK SIG4 Reacquire",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunway = true,
            },
        };
        AircraftState aircraft = SpawnParked(layout, stand);
        engine.World.AddAircraft(aircraft);

        CommandResult taxi = engine.SendCommand(aircraft.Callsign, "TAXIAUTO 28R");
        Assert.True(taxi.Success, $"'TAXIAUTO 28R' was refused: {taxi.Message}");

        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        int second = 0;
        NavTickDiag diag;
        do
        {
            second++;
            engine.TickOneSecond();
            diag = Assert.IsType<NavTickDiag>(aircraft.Ground.LastNavDiag);
        } while (diag.OnArc && (second < TickSeconds));

        Assert.False(diag.OnArc, $"the B738 was still on the stand-exit arc after {TickSeconds}s");
        int straightSegment = route.CurrentSegmentIndex;
        double offFt = OffLineFt(aircraft, diag, route);
        output.WriteLine($"t={second}s straight after the stand turn: {offFt:F1} ft off its line, GS {aircraft.GroundSpeed:F2} kt");
        Assert.True(
            offFt > GroundNavigator.ReacquireOffsetFt,
            $"the stand turn finished {offFt:F1} ft off the outgoing centreline: nothing to re-acquire"
        );

        while ((offFt > GroundNavigator.ReacquireOffsetFt) && (route.CurrentSegmentIndex == straightSegment) && (second < TickSeconds))
        {
            Assert.True(
                (diag.TargetSpeedKts <= GroundNavigator.ReacquireSpeedKts)
                    && (aircraft.GroundSpeed <= GroundNavigator.ReacquireSpeedKts + PhysicsSettleKts),
                $"t={second}s: {offFt:F1} ft off the line at GS {aircraft.GroundSpeed:F2} kt "
                    + $"(target {diag.TargetSpeedKts:F2}), above the re-acquire speed"
            );
            second++;
            engine.TickOneSecond();
            diag = Assert.IsType<NavTickDiag>(aircraft.Ground.LastNavDiag);
            offFt = OffLineFt(aircraft, diag, route);
            output.WriteLine($"t={second}s {offFt:F1} ft off, GS {aircraft.GroundSpeed:F2} kt, target {diag.TargetSpeedKts:F2} kt");
        }

        Assert.True(
            (route.CurrentSegmentIndex == straightSegment) && (offFt <= GroundNavigator.ReacquireOffsetFt),
            $"the B738 left the straight or ran out of time {offFt:F1} ft off its line"
        );

        // The diagnostic carries the target set before the tick's last move, which may still have been read off the line.
        engine.TickOneSecond();
        diag = Assert.IsType<NavTickDiag>(aircraft.Ground.LastNavDiag);
        Assert.True(
            diag.TargetSpeedKts > GroundNavigator.ReacquireSpeedKts,
            $"a second after regaining the line the target speed is still {diag.TargetSpeedKts:F2} kt"
        );
    }

    /// <summary>The aircraft's distance (ft) from the line the navigator's current straight runs on.</summary>
    private static double OffLineFt(AircraftState aircraft, NavTickDiag diag, TaxiRoute route)
    {
        var from = new LatLon(diag.SegFromLat, diag.SegFromLon);
        LatLon to = route.Segments[route.CurrentSegmentIndex].Edge.ToNode.Position;
        var heading = new TrueHeading(GeoMath.BearingTo(from, to));
        return Math.Abs(GeoMath.SignedCrossTrackDistanceNm(aircraft.Position, from, heading)) * GeoMath.FeetPerNm;
    }

    private static AircraftState SpawnParked(AirportGroundLayout layout, GroundNode stand)
    {
        var aircraft = new AircraftState
        {
            Callsign = "EDG320",
            AircraftType = "B738",
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "OAK",
                Destination = "OAK",
                FlightRules = "VFR",
                Altitude = PlannedAltitude.Vfr(1500),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
