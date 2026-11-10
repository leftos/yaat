using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Data.Faa;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// The nose-at-spot stop (<c>TaxiingPhase.TryStopNoseAtSpot</c>) measures the half-length setback along the route the aircraft
/// flies, not as the straight line to the spot node. SFO <c>TAXI $10</c> from stand G10 resolves to <c>T9 B5 $10</c>, whose final
/// leg is the B5 - T9 fillet arc ending on the spot node: a turn of about 130 degrees over 107 ft. Partway round that arc the
/// chord to the spot drops to a B738's half-length while the arc still has about 9 ft more to go, so a straight-line stop leaves
/// the nose short of the spot marking.
/// </summary>
public sealed class SpotNoseStopAlongRouteTests(ITestOutputHelper output)
{
    private const string Callsign = "SPT10";

    /// <summary>Tolerance (ft) on the at-rest setback: the stop lands within one physics sub-tick of travel at the spot crawl.</summary>
    private const double SetbackToleranceFt = 3.0;

    [Fact]
    public void TaxiToSfoSpot10_FromG10_StopsNoseAtSpotAlongTheFinalArc()
    {
        TestVnasData.EnsureInitialized();
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("SFO");
        if (layout is null)
        {
            return;
        }

        SimLogBuilder.CreateForTest(output).EnableCategory("TaxiingPhase", LogLevel.Debug).InitializeSimLog();

        GroundNode stand = GroundNodeLookup.Parking(layout, "G10");
        GroundNode? spot = layout.FindSpotNodeByName("10");
        Assert.NotNull(spot);

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "yaat69-spot10",
                ScenarioName = "yaat69-spot10",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "SFO",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };

        var aircraft = new AircraftState
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Position = stand.Position,
            TrueHeading = stand.TrueHeading ?? new TrueHeading(0),
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "SFO",
                Destination = "KLAX",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(30000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(new AtParkingPhase());
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);

        CommandResult cmd = engine.SendCommand(Callsign, "TAXI $10");
        Assert.True(cmd.Success, $"TAXI $10 failed: {cmd.Message}");

        TaxiRoute? route = aircraft.Ground.AssignedTaxiRoute;
        Assert.NotNull(route);
        TaxiRouteSegment lastLeg = route.Segments[^1];
        Assert.Equal(spot.Id, lastLeg.ToNodeId);
        GroundArc finalArc = Assert.IsType<GroundArc>(lastLeg.Edge.Edge);

        double halfLengthFt = AircraftLength.ResolveFt("B738") / 2.0;

        AircraftState? atRest = null;
        for (int t = 1; t <= 240; t++)
        {
            engine.TickOneSecond();
            AircraftState? ac = engine.FindAircraft(Callsign);
            Assert.NotNull(ac);
            if ((ac.Phases?.CurrentPhase is HoldingInPositionPhase or AtParkingPhase) && (ac.GroundSpeed < 0.5))
            {
                atRest = ac;
                break;
            }
        }

        Assert.NotNull(atRest);
        (double alongToSpotFt, double offArcFt) = finalArc.Locate(lastLeg.Edge.ToNode, atRest.Position);
        double straightToSpotFt = GeoMath.DistanceNm(atRest.Position, spot.Position) * GeoMath.FeetPerNm;
        output.WriteLine(
            $"B738 at rest: {alongToSpotFt:F1} ft along the final arc to spot 10 ({offArcFt:F1} ft off it), "
                + $"{straightToSpotFt:F1} ft straight; half-length {halfLengthFt:F1} ft."
        );

        Assert.True(offArcFt < 5.0, $"aircraft came to rest {offArcFt:F1} ft off the final B5 - T9 arc; the reproduction geometry has drifted.");
        Assert.True(
            Math.Abs(alongToSpotFt - halfLengthFt) <= SetbackToleranceFt,
            $"aircraft came to rest {alongToSpotFt:F1} ft along the route from spot 10 ({straightToSpotFt:F1} ft straight); the nose-at-spot "
                + $"stop should leave it a half-length ({halfLengthFt:F1} ft) short along the route, within {SetbackToleranceFt} ft. "
                + "More than that means the stop measured the chord to the spot node, so the nose stopped short of the marking."
        );
    }
}
