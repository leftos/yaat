using System.Globalization;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Approach;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Commands;

/// <summary>
/// AIM 5-3-8(j)(4)(a): an omitted <c>HOLDP</c> leg length is the altitude standard — 1 minute at or below 14,000 ft MSL,
/// 1½ minutes above — taken from the aircraft's assigned altitude, else its present altitude. The leg is resolved once, when
/// the hold is dispatched, and an explicit leg always wins.
/// </summary>
public class HoldingPatternStandardLegTests
{
    private const string Fix = "SUNOL";

    private readonly ITestOutputHelper _output;

    public HoldingPatternStandardLegTests(ITestOutputHelper output)
    {
        _output = output;
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Holdp_NoLeg_Assigned14000_FliesOneMinuteLegs() =>
        AssertLeg("HOLDP SUNOL 184 R", assignedAltitude: 14000, presentAltitude: 5000, expectedLegMinutes: 1.0);

    [Fact]
    public void Holdp_NoLeg_Assigned15000_FliesOneAndAHalfMinuteLegs() =>
        AssertLeg("HOLDP SUNOL 184 R", assignedAltitude: 15000, presentAltitude: 5000, expectedLegMinutes: 1.5);

    [Fact]
    public void Holdp_NoLeg_NoAssignment_Present14000_FliesOneMinuteLegs() =>
        AssertLeg("HOLDP SUNOL 184 R", assignedAltitude: null, presentAltitude: 14000, expectedLegMinutes: 1.0);

    [Fact]
    public void Holdp_NoLeg_NoAssignment_Present14001_FliesOneAndAHalfMinuteLegs() =>
        AssertLeg("HOLDP SUNOL 184 R", assignedAltitude: null, presentAltitude: 14001, expectedLegMinutes: 1.5);

    [Fact]
    public void Holdp_NoLeg_AssignedBelowPresent_UsesTheAssignedAltitude() =>
        AssertLeg("HOLDP SUNOL 184 R", assignedAltitude: 5000, presentAltitude: 16000, expectedLegMinutes: 1.0);

    [Fact]
    public void Holdp_ExplicitOneMinuteLeg_Above14000_KeepsTheExplicitLeg() =>
        AssertLeg("HOLDP SUNOL 184 1M R", assignedAltitude: null, presentAltitude: 16000, expectedLegMinutes: 1.0);

    private void AssertLeg(string command, double? assignedAltitude, double presentAltitude, double expectedLegMinutes)
    {
        SimulationEngine? engine = BuildEngine();
        if (engine is null)
        {
            _output.WriteLine("Skipped: OAK layout not available");
            return;
        }

        LatLon fix = FixPosition(Fix);
        AircraftState ac = AddAirborne(engine, "STD01", GeoMath.ProjectPoint(fix, new TrueHeading(0), 8), presentAltitude);
        ac.Targets.AssignedAltitude = assignedAltitude;

        CommandResult result = engine.SendCommand(ac.Callsign, command);
        Assert.True(result.Success, result.Message);

        HoldingPatternPhase phase = Assert.IsType<HoldingPatternPhase>(ac.Phases?.CurrentPhase);
        Assert.True(phase.IsMinuteBased);
        Assert.Equal(expectedLegMinutes, phase.LegLength);
        Assert.Contains($"{expectedLegMinutes.ToString(CultureInfo.InvariantCulture)}min legs", result.Message);
    }

    private static LatLon FixPosition(string fixName)
    {
        (double Lat, double Lon) position = Assert.IsType<(double Lat, double Lon)>(NavigationDatabase.Instance.GetFixPosition(fixName));
        return new LatLon(position.Lat, position.Lon);
    }

    private SimulationEngine? BuildEngine()
    {
        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("OAK") is not { } layout)
        {
            return null;
        }

        SimLogBuilder.CreateForTest(_output).InitializeSimLog();

        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped())
        {
            Scenario = new SimScenarioState
            {
                ScenarioId = "t",
                ScenarioName = "t",
                RngSeed = 42,
                OriginalScenarioJson = "{}",
                PrimaryAirportId = "OAK",
            },
        };
        engine.World.GroundLayout = layout;
        engine.World.ReactionDelayRng = new SerializableRandom(42);
        return engine;
    }

    private static AircraftState AddAirborne(SimulationEngine engine, string callsign, LatLon position, double altitudeFt)
    {
        var ac = new AircraftState
        {
            Callsign = callsign,
            AircraftType = "B738",
            Position = position,
            TrueHeading = new TrueHeading(180),
            Altitude = altitudeFt,
            IndicatedAirspeed = 210,
            IsOnGround = false,
            FlightPlan = new AircraftFlightPlan { Departure = "OAK", Destination = "MEM" },
        };
        engine.World.AddAircraft(ac);
        return ac;
    }
}
