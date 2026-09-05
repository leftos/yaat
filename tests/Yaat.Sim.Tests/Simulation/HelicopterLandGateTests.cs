using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
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
        var layout = groundData.GetLayout("OAK");
        Assert.NotNull(layout);

        var engine = new SimulationEngine(groundData)
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
        heli.Phases.Start(CommandDispatcher.BuildMinimalContext(heli, layout));
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
        var engine = new SimulationEngine(groundData)
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

        var result = engine.SendCommand(heli.Callsign, "LAND @SIG1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void Land_FromOffField_InstallsApproach()
    {
        var (engine, _, heli) = Setup(OverTheBay, altitude: 500, onGround: false);

        var result = engine.SendCommand(heli.Callsign, "LAND @SIG1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
        Assert.Equal("SIG1", heli.Ground.ParkingSpot);
    }

    [Fact]
    public void Land_HoveringOverTheField_AtPatternAltitude_AirTaxis()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        var (engine, _, heli) = Setup(heliSpot.Position, altitude: OakFieldElevationFt + 300, onGround: false);

        var result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void Land_OnTheGround_AirTaxis()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        var (engine, _, heli) = Setup(heliSpot.Position, altitude: OakFieldElevationFt, onGround: true);

        var result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<AirTaxiPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void Land_OverTheField_AbovePatternAltitude_InstallsApproach()
    {
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var heliSpot = layout!.FindSpotByName("HELI");
        Assert.NotNull(heliSpot);
        var (engine, _, heli) = Setup(heliSpot.Position, altitude: OakFieldElevationFt + 1500, onGround: false);

        var result = engine.SendCommand(heli.Callsign, "LAND @FDX1");

        Assert.True(result.Success, result.Message);
        Assert.IsType<HelicopterApproachPhase>(heli.Phases!.CurrentPhase);
    }

    [Fact]
    public void AirTaxi_FromOffField_IsRefused_AndKeepsThePhase()
    {
        var (engine, _, heli) = Setup(OverTheBay, altitude: 500, onGround: false);

        var result = engine.SendCommand(heli.Callsign, "ATXI SIG1");

        Assert.False(result.Success);
        // Spoken by the pilot as the "unable" readback: short, about the aircraft, no dash for the verbalizer to choke on.
        Assert.Matches(@"^Unable, we're \d+ miles out, request landing at SIG1$", result.Message);
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
        var layout = new TestAirportGroundData().GetLayout("OAK");
        var sig1 = layout!.FindSpotByName("SIG1");
        Assert.NotNull(sig1);
        var twoMilesWest = GeoMath.ProjectPoint(sig1.Position, new TrueHeading(270), 2.0);
        var (engine, _, heli) = Setup(twoMilesWest, altitude: 2000, onGround: false);

        var result = engine.SendCommand(heli.Callsign, "LAND @SIG1");
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
    public void TopOfDescent_IsTransitDropPlusBuffer(double holdAltitude, double fieldElevation, double expectedNm)
    {
        Assert.Equal(expectedNm, HelicopterApproachPhase.TopOfDescentNm(holdAltitude, fieldElevation), 2);
    }

    [Fact]
    public void FinalStart_Is400FtDropOnTheSixDegreePath()
    {
        // 500 ft AGL pattern altitude down to the 100 ft AGL air-taxi height over the spot at 6° ≈ 0.63 nm.
        Assert.Equal(0.626, HelicopterApproachPhase.FinalStartNm(AircraftCategory.Helicopter), 2);
    }
}
