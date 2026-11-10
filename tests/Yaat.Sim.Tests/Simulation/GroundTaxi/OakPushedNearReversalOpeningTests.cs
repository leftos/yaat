using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// A B38M just pushed off OAK stand 4 rests in <see cref="HoldingAfterPushbackPhase"/> nearly reversed from its route:
/// <c>TAXI TC U W W1</c> resolves a 21–50 ft free-space approach leg, the 62.1 ft RAMP straight 907→906 and the 15.3 ft
/// <c>TC - RAMP</c> fillet 906→900. The free-space entry arc aimed at node 906, the far end of the short straight; once
/// the arc retired the straight, a fresh setup turned again onto the straight's own bearing, arrived ~40 ft abeam 906
/// and started the fillet ~39 ft off its curve — a teleport. The opening arc must be aimed past that short straight, so
/// the aircraft rolls out on the line to the fillet's end and flies onto it.
/// </summary>
public sealed class OakPushedNearReversalOpeningTests(ITestOutputHelper output)
{
    private const string Callsign = "SWA314";
    private const string AircraftType = "B38M";
    private const string AirportId = "OAK";
    private const string Command = "TAXI TC U W W1";
    private const string FilletName = "TC - RAMP";

    /// <summary>The just-pushed pose from the grid run that threw.</summary>
    private const double PushedLat = 37.7112326773202;
    private const double PushedLon = -122.21375863060773;
    private const double PushedHeadingDeg = 323.0;

    private const int WindowSec = 60;

    /// <summary>The short painted straight the opening arc must aim past, as the grid run found it.</summary>
    private const string ShortStraightName = "RAMP";

    private const int ShortStraightFromNodeId = 907;
    private const int ShortStraightToNodeId = 906;
    private const double ShortStraightFt = 62.1;
    private const double ShortStraightToleranceFt = 2.0;

    /// <summary>Knots to feet per second.</summary>
    private const double FtPerSecPerKt = GeoMath.FeetPerNm / 3600.0;

    /// <summary>How close the aircraft must come to the fillet's end node, feet.</summary>
    private const double FilletEndReachFt = 6.0;

    [Fact]
    public void TaxiAfterPush_NearReversalOntoShortStraight_FliesOntoTheFilletWithoutTeleport()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout(AirportId);
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).InitializeSimLog();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-oak-pushed-near-reversal",
                ScenarioName = "OAK pushed near-reversal opening",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = AirportId,
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };
        AircraftState aircraft = MakeAircraft(layout);
        engine.World.AddAircraft(aircraft);

        CommandResult result = engine.SendCommand(Callsign, Command);
        Assert.True(result.Success, $"'{Command}' failed: {result.Message}");
        TaxiRoute route = Assert.IsType<TaxiRoute>(aircraft.Ground.AssignedTaxiRoute);
        output.WriteLine($"route: {route.ToSummary()} ({route.Segments.Count} segments)");

        TaxiRouteSegment straight = route.Segments[1];
        double straightFt = straight.Edge.DistanceNm * GeoMath.FeetPerNm;
        output.WriteLine($"seg[1] {straight.FromNodeId} -> {straight.ToNodeId} on {straight.TaxiwayName}, {straightFt:F1} ft");
        Assert.True(
            (straight.Edge.Edge is not GroundArc)
                && (straight.TaxiwayName == ShortStraightName)
                && (straight.FromNodeId == ShortStraightFromNodeId)
                && (straight.ToNodeId == ShortStraightToNodeId)
                && (Math.Abs(straightFt - ShortStraightFt) <= ShortStraightToleranceFt),
            $"expected the {ShortStraightName} straight {ShortStraightFromNodeId}→{ShortStraightToNodeId} of ~{ShortStraightFt} ft "
                + "after the approach leg; the route does not exercise the short-straight opening"
        );

        TaxiRouteSegment fillet = route.Segments.First(s => s.Edge.Edge is GroundArc);
        Assert.Equal(FilletName, fillet.TaxiwayName);
        LatLon filletEnd = fillet.Edge.ToNode.Position;

        double closestFt = FeetBetween(aircraft.Position, filletEnd);
        for (int t = 1; t <= WindowSec; t++)
        {
            LatLon before = aircraft.Position;
            double iasBefore = aircraft.IndicatedAirspeed;
            engine.TickOneSecond();
            double stepFt = FeetBetween(before, aircraft.Position);
            double limitFt = ((Math.Max(iasBefore, aircraft.IndicatedAirspeed) + 1.0) * FtPerSecPerKt) + 2.0;
            double toEndFt = FeetBetween(aircraft.Position, filletEnd);
            closestFt = Math.Min(closestFt, toEndFt);
            output.WriteLine(
                $"t+{t, 2}: hdg={aircraft.TrueHeading.Degrees, 5:F0} ias={aircraft.IndicatedAirspeed, 5:F1} step={stepFt, 5:F1}ft "
                    + $"toFilletEnd={toEndFt, 6:F1}ft seg={route.CurrentSegmentIndex}"
            );
            Assert.True(stepFt <= limitFt, $"t+{t}: {Callsign} moved {stepFt:F1} ft in one second, more than its speed allows ({limitFt:F1} ft)");
        }

        Assert.True(
            closestFt <= FilletEndReachFt,
            $"{Callsign} came no closer than {closestFt:F1} ft to the {FilletName} fillet's end; it never flew onto the fillet"
        );
    }

    private static double FeetBetween(LatLon a, LatLon b) => GeoMath.DistanceNm(a, b) * GeoMath.FeetPerNm;

    private static AircraftState MakeAircraft(AirportGroundLayout layout)
    {
        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = AircraftType,
            Position = new LatLon(PushedLat, PushedLon),
            TrueHeading = new TrueHeading(PushedHeadingDeg),
            Altitude = 9,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = AirportId,
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new HoldingAfterPushbackPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        return aircraft;
    }
}
