using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The LAND / ATXI gate between the two helicopter arrival profiles on the real OAK layout: on the
/// field (on the ground, or hovering over it at or below the 500 ft rotorcraft pattern altitude) the
/// spot is reached by an air taxi; from off the field LAND installs an approach and ATXI is refused,
/// because air taxi is a ground movement on the airport (AIM §4-3-17.b; 7110.65 §3-11-1.c NOTE) while a
/// landing clearance to a spot is an approach (§3-11-6).
/// </summary>
public class HelicopterLandGateTests
{
    private const double OakFieldElevationFt = 9.0;

    // ~9.6 nm west-northwest of the OAK north field, over the bay — where the S2-OAK-5 R22 was told to land.
    private static readonly LatLon OverTheBay = new(37.8199, -122.3714);

    public HelicopterLandGateTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static (SimulationEngine Engine, AirportGroundLayout Layout, AircraftState Heli) Setup(LatLon position, double altitude, bool onGround)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout? layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-helicopter-land-gate",
                ScenarioName = "Helicopter LAND gate",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };

        var heli = new AircraftState
        {
            Callsign = "N20662",
            AircraftType = "R22",
            Position = position,
            TrueHeading = new TrueHeading(125),
            TrueTrack = new TrueHeading(125),
            Altitude = altitude,
            IndicatedAirspeed = 0,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
        };
        heli.Ground.Layout = layout;
        heli.Phases = new PhaseList();
        heli.Phases.Add(onGround ? new AtParkingPhase() : new VfrHoldPhase());
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, ActiveRunways.Empty, layout));
        engine.World.AddAircraft(heli);
        return (engine, layout, heli);
    }

    /// <summary>
    /// A helicopter with no active phase and no cached ground layout (an airborne spawn after a heading or
    /// altitude instruction) must still resolve the airport from its flight plan: LAND is dispatched through
    /// the no-phase arm, which used to pass the raw cached layout (null) and refuse the command.
    /// </summary>
    [Fact]
    public void Land_WithNoPhaseAndNoCachedLayout_ResolvesTheAirport()
    {
        var groundData = new TestAirportGroundData();
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-helicopter-land-gate",
                ScenarioName = "Helicopter LAND gate",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        var heli = new AircraftState
        {
            Callsign = "N20662",
            AircraftType = "R22",
            Position = OverTheBay,
            TrueHeading = new TrueHeading(125),
            TrueTrack = new TrueHeading(125),
            Altitude = 1000,
            IndicatedAirspeed = 90,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "KOAK", Destination = "KOAK" },
        };
        engine.World.AddAircraft(heli);
        Assert.Null(heli.Ground.Layout);
        Assert.Null(heli.Phases);

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @SIG1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void Land_FromOffField_InstallsApproach()
    {
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(OverTheBay, altitude: 500, onGround: false);

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @SIG1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
        Assert.Equal("SIG1", heli.Ground.ParkingSpot);
    }

    [Fact]
    public void Land_HoveringOverTheField_AtPatternAltitude_AirTaxis()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt + 300,
            onGround: false
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void Land_OnTheGround_AirTaxis()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt,
            onGround: true
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
    }

    /// <summary>
    /// <c>LAND $C</c> names KOAK's taxiway spot C, never the parking position C that <c>LAND @C</c> reaches
    /// first: the hovering helicopter lands at the spot node.
    /// </summary>
    [Fact]
    public void Land_DollarMarker_LandsAtTheTaxiSpot_NotTheSameNamedParking()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        GroundNode? spotC = layout.FindSpotNodeByName("C");
        GroundNode? parkingC = layout.FindParkingByName("C");
        Assert.NotNull(heliSpot);
        Assert.NotNull(spotC);
        Assert.NotNull(parkingC);
        Assert.True(
            GeoMath.DistanceNm(spotC.Position, parkingC.Position) > 0.1,
            "KOAK's spot C and parking C must be apart for this test to tell them apart."
        );
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt + 300,
            onGround: false
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND $C");

        Assert.True(result.Success, result.Message);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
        for (int t = 0; (t < 900) && (heli.Phases?.CurrentPhase is not HoldingInPositionPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<HoldingInPositionPhase>(heli.Phases!.CurrentPhase);
        Assert.True(heli.IsOnGround, "The helicopter should have landed.");
        double fromSpot = GeoMath.DistanceNm(heli.Position, spotC.Position);
        Assert.True(fromSpot < 0.05, $"Landed {fromSpot:F3} nm from spot C; LAND $C must land at the taxiway spot.");
    }

    /// <summary>
    /// The Land flyout's form for a helicopter bound elsewhere: airborne bound for KSFO with SFO's layout cached,
    /// <c>APT OAK, LAND @HELI1</c> switches it to KOAK's layout before LAND resolves the name, so it lands at KOAK's
    /// HELI1 rather than refusing the name or finding it at SFO.
    /// </summary>
    [Fact]
    public void AptThenLand_BoundElsewhereWithThatLayoutCached_LandsAtTheNamedAirport()
    {
        (SimulationEngine engine, AirportGroundLayout oak, AirportGroundLayout _, AircraftState heli) = BoundForSfo(onGround: false);
        GroundNode heli1 = oak.FindParkingByName("HELI1")!;

        CommandResult result = engine.SendCommand(heli.Callsign, "APT OAK, LAND @HELI1");

        Assert.True(result.Success, result.Message);
        Assert.Equal(oak.AirportId, heli.Ground.Layout?.AirportId);
        for (int t = 0; (t < 900) && (heli.Phases?.CurrentPhase is not AtParkingPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<AtParkingPhase>(heli.Phases!.CurrentPhase);
        double fromHeli1 = GeoMath.DistanceNm(heli.Position, heli1.Position);
        Assert.True(fromHeli1 < 0.05, $"Landed {fromHeli1:F3} nm from KOAK's HELI1.");
    }

    /// <summary>
    /// <c>APT</c> alone switches an airborne aircraft's ground layout to the new destination's, and leaves an aircraft on
    /// the ground on the layout its wheels are on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apt_SwitchesTheLayout_OnlyWhenAirborne(bool onGround)
    {
        (SimulationEngine engine, AirportGroundLayout oak, AirportGroundLayout sfo, AircraftState heli) = BoundForSfo(onGround);

        CommandResult result = engine.SendCommand(heli.Callsign, "APT OAK");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KOAK", heli.FlightPlan.Destination);
        Assert.Equal((onGround ? sfo : oak).AirportId, heli.Ground.Layout?.AirportId);
    }

    /// <summary>A scenario preset <c>APT OAK, LAND @HELI1</c> switches the layout as the typed form does and lands at KOAK's HELI1.</summary>
    [Fact]
    public void PresetAptThenLand_BoundElsewhereWithThatLayoutCached_LandsAtTheNamedAirport()
    {
        (SimulationEngine engine, AirportGroundLayout oak, AirportGroundLayout _, AircraftState heli) = BoundForSfo(onGround: false);

        engine.DispatchPresetCommands(
            new LoadedAircraft { State = heli, PresetCommands = [new PresetCommand { Command = "APT OAK, LAND @HELI1", TimeOffset = 0 }] }
        );

        AssertLandsAtHeli1(engine, oak, heli);
    }

    /// <summary>
    /// A triggered block <c>LV 5 APT OAK, LAND @HELI1</c> runs its <c>LAND</c> on the layout its own <c>APT</c> switched to
    /// when it fires, and lands at KOAK's HELI1.
    /// </summary>
    [Fact]
    public void TriggeredAptThenLand_BoundElsewhereWithThatLayoutCached_LandsAtTheNamedAirport()
    {
        (SimulationEngine engine, AirportGroundLayout oak, AirportGroundLayout _, AircraftState heli) = BoundForSfo(onGround: false);

        CommandResult result = engine.SendCommand(heli.Callsign, "LV 5 APT OAK, LAND @HELI1");

        Assert.True(result.Success, result.Message);
        AssertLandsAtHeli1(engine, oak, heli);
    }

    /// <summary>
    /// Low over KOAK on KOAK's layout, air-taxiing to FDX1, a helicopter given <c>APT SFO</c> keeps KOAK's layout: the field
    /// under it is the one its ground phases run on. The destination still changes.
    /// </summary>
    [Fact]
    public void Apt_LowOverTheFieldItsLayoutCovers_KeepsThatLayout()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt + 300,
            onGround: false
        );
        Assert.True(engine.SendCommand(heli.Callsign, "LAND @FDX1").Success);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);

        CommandResult result = engine.SendCommand(heli.Callsign, "APT SFO");

        Assert.True(result.Success, result.Message);
        Assert.Equal("KSFO", heli.FlightPlan.Destination);
        Assert.Equal("OAK", heli.Ground.Layout?.AirportId);
    }

    private static void AssertLandsAtHeli1(SimulationEngine engine, AirportGroundLayout oak, AircraftState heli)
    {
        GroundNode heli1 = oak.FindParkingByName("HELI1")!;
        for (int t = 0; (t < 900) && (heli.Phases?.CurrentPhase is not AtParkingPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.Equal(oak.AirportId, heli.Ground.Layout?.AirportId);
        Assert.IsType<AtParkingPhase>(heli.Phases!.CurrentPhase);
        double fromHeli1 = GeoMath.DistanceNm(heli.Position, heli1.Position);
        Assert.True(fromHeli1 < 0.05, $"Landed {fromHeli1:F3} nm from KOAK's HELI1.");
    }

    /// <summary>
    /// An EC35 bound for KSFO with SFO's layout cached: airborne 1.2 nm south of KOAK's HELI1 at 500 ft in a VFR hold, or on
    /// the ground at SFO.
    /// </summary>
    private static (SimulationEngine Engine, AirportGroundLayout Oak, AirportGroundLayout Sfo, AircraftState Heli) BoundForSfo(bool onGround)
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout oak = groundData.GetLayout("OAK")!;
        AirportGroundLayout sfo = groundData.GetLayout("SFO")!;
        Assert.NotNull(oak);
        Assert.NotNull(sfo);
        GroundNode heli1 = oak.FindParkingByName("HELI1")!;
        Assert.NotNull(heli1);
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "test-helicopter-apt-land",
                ScenarioName = "Helicopter APT then LAND",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        LatLon position = onGround
            ? sfo.Nodes.Values.First(n => n.Type == GroundNodeType.Parking).Position
            : GeoMath.ProjectPoint(heli1.Position, new TrueHeading(180), 1.2);
        var heli = new AircraftState
        {
            Callsign = "N812HL",
            AircraftType = "EC35",
            Position = position,
            TrueHeading = new TrueHeading(0),
            TrueTrack = new TrueHeading(0),
            Altitude = onGround ? 13 : 500,
            IndicatedAirspeed = 0,
            IsOnGround = onGround,
            FlightPlan = new AircraftFlightPlan { Departure = "KSFO", Destination = "KSFO" },
        };
        heli.Ground.Layout = sfo;
        heli.Phases = new PhaseList();
        heli.Phases.Add(onGround ? new AtParkingPhase() : new VfrHoldPhase());
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, ActiveRunways.Empty, sfo));
        engine.World.AddAircraft(heli);
        return (engine, oak, sfo, heli);
    }

    /// <summary>
    /// <c>LAND $I8R</c> ends as <c>ATXI $I8R</c> does: a taxiway spot is not a parking position, so the helicopter lands and
    /// holds in position there with no parking spot set, never reported as parked on the taxiway.
    /// </summary>
    [Fact]
    public void Land_DollarMarker_HoldsInPositionAtTheSpot_WithNoParkingSpot()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        GroundNode? spotI8R = layout.FindSpotNodeByName("I8R");
        Assert.NotNull(heliSpot);
        Assert.NotNull(spotI8R);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt + 300,
            onGround: false
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND $I8R");

        Assert.True(result.Success, result.Message);
        Assert.Null(heli.Ground.ParkingSpot);
        for (int t = 0; (t < 900) && (heli.Phases?.CurrentPhase is AirTaxiPhase or HelicopterLandingPhase); t++)
        {
            engine.TickOneSecond();
        }

        Assert.IsType<HoldingInPositionPhase>(heli.Phases!.CurrentPhase);
        Assert.True(heli.IsOnGround, "The helicopter should have landed.");
        Assert.Null(heli.Ground.ParkingSpot);
        double fromSpot = GeoMath.DistanceNm(heli.Position, spotI8R.Position);
        Assert.True(fromSpot < 0.05, $"Landed {fromSpot:F3} nm from spot I8R.");
    }

    [Fact]
    public void Land_OverTheField_AbovePatternAltitude_InstallsApproach()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            heliSpot.Position,
            altitude: OakFieldElevationFt + 1500,
            onGround: false
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void AirTaxi_FromOffField_IsRefused_AndKeepsThePhase()
    {
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(OverTheBay, altitude: 500, onGround: false);

        CommandResult result = engine.SendCommand(heli.Callsign, "ATXI @SIG1");

        Assert.False(result.Success);
        // Spoken by the pilot as the "unable" readback: short, about the aircraft, no dash for the verbalizer to choke on.
        Assert.Matches(@"^Unable, we're \d+ miles out, request landing at SIG1$", result.Message);
        Assert.IsType<VfrHoldPhase>(heli.Phases!.CurrentPhase);
    }

    /// <summary>
    /// Over the field but above the air-taxi ceiling: the refusal is the same, but a rounded mileage would read
    /// "0 miles out". The heli says where it is instead.
    /// </summary>
    [Fact]
    public void AirTaxi_FromOverheadTooHigh_IsRefused_WithoutAZeroMileage()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? sig1 = layout!.FindSpotByName("SIG1");
        Assert.NotNull(sig1);

        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(
            sig1.Position,
            altitude: OakFieldElevationFt + 600,
            onGround: false
        );

        CommandResult result = engine.SendCommand(heli.Callsign, "ATXI @SIG1");

        Assert.False(result.Success);
        Assert.Equal("Unable, we're overhead, request landing at SIG1", result.Message);
        Assert.IsType<VfrHoldPhase>(heli.Phases!.CurrentPhase);
    }

    /// <summary>
    /// A clearance received inside top of descent (2 nm out at 2000 ft) must still capture the pattern
    /// altitude before the final gate and arrive over the spot at the air-taxi height — never hand a
    /// hover hundreds of feet up to the landing phase's vertical descent.
    /// </summary>
    [Fact]
    public void Land_FromTwoMilesHigh_CapturesThePathBeforeTheSpot()
    {
        AirportGroundLayout? layout = new TestAirportGroundData().GetLayout("OAK");
        GroundNode? sig1 = layout!.FindSpotByName("SIG1");
        Assert.NotNull(sig1);
        LatLon twoMilesWest = GeoMath.ProjectPoint(sig1.Position, new TrueHeading(270), 2.0);
        (SimulationEngine? engine, AirportGroundLayout _, AircraftState? heli) = Setup(twoMilesWest, altitude: 2000, onGround: false);

        CommandResult result = engine.SendCommand(heli.Callsign, "LAND @SIG1");
        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);

        double? handoffAgl = null;
        for (int t = 0; t < 900; t++)
        {
            engine.TickOneSecond();
            if (heli.Phases?.CurrentPhase is HelicopterLandingPhase or AtParkingPhase)
            {
                handoffAgl = heli.Altitude - OakFieldElevationFt;
                break;
            }
        }

        Assert.True(handoffAgl is not null, "The approach never handed off to the landing within 15 minutes.");
        Assert.True(handoffAgl <= 150, $"Handed off to the landing {handoffAgl:F0} ft AGL over the spot — the approach must capture the path first.");
    }

    [Theory]
    [InlineData(500.0, 9.0, 1.0)]
    [InlineData(1509.0, 9.0, 3.5)]
    public void TopOfDescent_IsTransitDropPlusBuffer(double holdAltitude, double fieldElevation, double expectedNm) =>
        Assert.Equal(expectedNm, HelicopterApproachPhase.TopOfDescentNm(holdAltitude, fieldElevation), 2);

    [Fact]
    public void FinalStart_Is400FtDropOnTheSixDegreePath() =>
        // 500 ft AGL pattern altitude down to the 100 ft AGL air-taxi height over the spot at 6° ≈ 0.63 nm.
        Assert.Equal(0.626, HelicopterApproachPhase.FinalStartNm(AircraftCategory.Helicopter), 2);
}
