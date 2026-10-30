using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.GroundTaxi;

/// <summary>
/// YAAT-711: FDX109 (B763) was told <c>TAXI W V B @FDX1</c> while FDX440 (B752) stood parked at FDX1. The ground
/// conflict detector stopped it short of the stand, as it should, but the controller was never told why. The TAXI
/// response now names the stand's occupant.
/// </summary>
public sealed class OakTaxiToOccupiedStandTests
{
    public OakTaxiToOccupiedStandTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void TaxiToAStandAnotherAircraftIsParkedOn_SaysWhoOccupiesItAndHoldsShort()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimulationEngine engine = NewEngine(groundData);
        GroundNode stand = layout.FindParkingByName("FDX1") ?? throw new InvalidOperationException("OAK has no stand FDX1");
        AircraftState occupant = Spawn(engine, layout, ("FDX440", "B752"), (stand, stand.TrueHeading ?? new TrueHeading(0)), new AtParkingPhase());
        occupant.Ground.ParkingSpot = "FDX1";
        AircraftState arrival = SpawnArrival(engine, layout);

        CommandResult taxi = engine.SendCommand("FDX109", "TAXI B @FDX1");
        Assert.True(taxi.Success, taxi.Message);
        Assert.Contains("[stand FDX1 occupied by FDX440]", taxi.Message);

        for (int second = 0; second < 600; second++)
        {
            engine.TickOneSecond();
        }

        double fromStandFt = GeoMath.DistanceNm(arrival.Position, stand.Position) * GeoMath.FeetPerNm;
        Assert.False(arrival.Phases?.CurrentPhase is AtParkingPhase, $"FDX109 parked on FDX1 on top of FDX440 ({fromStandFt:F0} ft from the stand)");
        Assert.Equal(0, arrival.GroundSpeed);
        Assert.True(fromStandFt > 100, $"FDX109 stopped {fromStandFt:F0} ft from FDX1, inside the occupied stand");
    }

    [Fact]
    public void TaxiToAFreeStand_SaysNothingAboutOccupancy()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        if (layout is null)
        {
            return;
        }

        SimulationEngine engine = NewEngine(groundData);
        SpawnArrival(engine, layout);

        CommandResult taxi = engine.SendCommand("FDX109", "TAXI B @FDX1");
        Assert.True(taxi.Success, taxi.Message);
        Assert.DoesNotContain("occupied by", taxi.Message);
    }

    /// <summary>
    /// The same occupied FDX1, the TAXI given while FDX109 is still rolling out on 28R, which stores the route for the end of
    /// the rollout: that response names the occupant too.
    /// </summary>
    [Fact]
    public void TaxiOnTheLandingRolloutToAStandAnotherAircraftIsParkedOn_SaysWhoOccupiesIt()
    {
        if (ShortFinalArrival.SpawnClearedToLand("OAK", "28R", "B763", "FDX109", 1.0) is not { } spawned)
        {
            return;
        }

        AircraftState arrival = spawned.Aircraft;
        AirportGroundLayout layout = Assert.IsType<AirportGroundLayout>(arrival.Ground.Layout);
        GroundNode stand = layout.FindParkingByName("FDX1") ?? throw new InvalidOperationException("OAK has no stand FDX1");
        AircraftState occupant = Spawn(
            spawned.Engine,
            layout,
            ("FDX440", "B752"),
            (stand, stand.TrueHeading ?? new TrueHeading(0)),
            new AtParkingPhase()
        );
        occupant.Ground.ParkingSpot = "FDX1";

        bool rollingOut = false;
        for (int second = 0; (second < 300) && !rollingOut; second++)
        {
            spawned.Engine.TickOneSecond();
            rollingOut = arrival.IsOnGround && (arrival.Phases?.CurrentPhase is LandingPhase);
        }

        Assert.True(rollingOut, $"FDX109 never touched down on 28R: {arrival.Phases?.CurrentPhase?.Name}");
        CommandResult taxi = spawned.Engine.SendCommand("FDX109", "TAXIAUTO @FDX1");
        Assert.True(taxi.Success, taxi.Message);
        Assert.IsType<LandingPhase>(arrival.Phases?.CurrentPhase);
        Assert.Contains("[stand FDX1 occupied by FDX440]", taxi.Message);
    }

    private static SimulationEngine NewEngine(TestAirportGroundData groundData) =>
        new(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "yaat-711",
                ScenarioName = "YAAT-711 TAXI to an occupied stand",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
                AutoCrossRunwayMode = AutoCrossRunwayMode.Off,
            },
        };

    private static AircraftState SpawnArrival(SimulationEngine engine, AirportGroundLayout layout)
    {
        GroundNode start = layout.FindIntersectionNode("V", "B") ?? throw new InvalidOperationException("OAK has no V/B junction");
        GroundNode b3 = layout.FindIntersectionNode("B", "B3") ?? throw new InvalidOperationException("OAK has no B/B3 junction");
        return Spawn(
            engine,
            layout,
            ("FDX109", "B763"),
            (start, new TrueHeading(GeoMath.BearingTo(start.Position, b3.Position))),
            new HoldingInPositionPhase()
        );
    }

    private static AircraftState Spawn(
        SimulationEngine engine,
        AirportGroundLayout layout,
        (string Callsign, string Type) who,
        (GroundNode Node, TrueHeading Heading) where,
        Phase phase
    )
    {
        var aircraft = new AircraftState
        {
            Callsign = who.Callsign,
            AircraftType = who.Type,
            Position = where.Node.Position,
            TrueHeading = where.Heading,
            Altitude = 0,
            IndicatedAirspeed = 0,
            IsOnGround = true,
            FlightPlan = new AircraftFlightPlan
            {
                Departure = "KMEM",
                Destination = "KOAK",
                FlightRules = "IFR",
                Altitude = PlannedAltitude.Ifr(35000),
            },
            Phases = new PhaseList(),
        };
        aircraft.Phases.Add(phase);
        aircraft.Phases.Start(CommandDispatcher.BuildMinimalContext(aircraft, ActiveRunways.Empty, layout));
        aircraft.Ground.Layout = layout;
        engine.World.AddAircraft(aircraft);
        return aircraft;
    }
}
