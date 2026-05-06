using System.Text.Json;
using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// Tests that VFR cold-call aircraft spawn without an assigned discrete beacon
/// code and squawking 1200 (FAA VFR conspicuity code), and that filed plans
/// keep their discrete code so they're trackable immediately. Covers both the
/// <see cref="AircraftGenerator"/> (ADD command) path and the
/// <see cref="ScenarioLoader"/> (scenario JSON) path.
/// </summary>
public class VfrColdCallSpawnTests
{
    [Fact]
    public void AircraftGenerator_VfrAdd_BearingSpawn_NoAssignedCode_Squawks1200()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = WeightClass.Small,
            Engine = EngineKind.Piston,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 360,
            DistanceNm = 10,
            Altitude = 4500,
        };

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, new Random(42));

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal((uint)0, state.Transponder.AssignedCode);
        Assert.Equal((uint)1200, state.Transponder.Code);
        // Airborne VFR ADD squawks Mode C (real-world airborne /1200 traffic is altitude-
        // reporting). Only parking spawns sit on Standby.
        Assert.Equal("C", state.Transponder.Mode);
        Assert.Equal("VFR", state.FlightPlan.FlightRules);
        Assert.False(state.FlightPlan.HasFlightPlan);
        Assert.Equal("", state.FlightPlan.Destination);
        Assert.Equal("", state.FlightPlan.Route);
    }

    [Fact]
    public void AircraftGenerator_VfrAdd_ParkingSpawn_TransponderOnStandby()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        // Parking spawns: pilot's transponder is on Standby until they power up for taxi.
        var groundLayout = new TestAirportGroundData().GetLayout("OAK");
        Assert.NotNull(groundLayout);

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Vfr,
            Weight = WeightClass.Small,
            Engine = EngineKind.Piston,
            PositionType = SpawnPositionType.Parking,
            ParkingName = "NEW1",
        };

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout, new Random(42));

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.Equal("Standby", state.Transponder.Mode);
        Assert.True(state.IsOnGround);
    }

    [Fact]
    public void AircraftGenerator_IfrAdd_BearingSpawn_GetsDiscreteCode()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var request = new SpawnRequest
        {
            Rules = FlightRulesKind.Ifr,
            Weight = WeightClass.Large,
            Engine = EngineKind.Jet,
            PositionType = SpawnPositionType.Bearing,
            Bearing = 270,
            DistanceNm = 30,
            Altitude = 11000,
        };

        var (state, error) = AircraftGenerator.Generate(request, "OAK", [], groundLayout: null, new Random(42));

        Assert.Null(error);
        Assert.NotNull(state);
        Assert.NotEqual((uint)0, state.Transponder.AssignedCode);
        Assert.NotEqual((uint)1200, state.Transponder.AssignedCode);
        Assert.Equal(state.Transponder.AssignedCode, state.Transponder.Code);
        Assert.Equal("C", state.Transponder.Mode);
        Assert.Equal("IFR", state.FlightPlan.FlightRules);
    }

    [Fact]
    public void ScenarioLoader_AircraftWithoutFlightPlan_NoAssignedCode_Squawks1200()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var scenarioJson = BuildSingleAircraftScenario(includeFlightPlan: false);

        var result = ScenarioLoader.Load(scenarioJson, groundData: null, new Random(42));
        var loaded = result.ImmediateAircraft.FirstOrDefault(a => a.State.Callsign == "N123XX");
        Assert.NotNull(loaded);

        var state = loaded.State;
        Assert.False(state.FlightPlan.HasFlightPlan);
        Assert.Equal((uint)0, state.Transponder.AssignedCode);
        Assert.Equal((uint)1200, state.Transponder.Code);
        Assert.Equal("VFR", state.FlightPlan.FlightRules);
        Assert.Equal("", state.FlightPlan.Destination);
    }

    [Fact]
    public void ScenarioLoader_AircraftWithFlightPlan_GetsDiscreteCode()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return;
        }

        var scenarioJson = BuildSingleAircraftScenario(includeFlightPlan: true);

        var result = ScenarioLoader.Load(scenarioJson, groundData: null, new Random(42));
        var loaded = result.ImmediateAircraft.FirstOrDefault(a => a.State.Callsign == "N123XX");
        Assert.NotNull(loaded);

        var state = loaded.State;
        Assert.True(state.FlightPlan.HasFlightPlan);
        Assert.NotEqual((uint)0, state.Transponder.AssignedCode);
        Assert.NotEqual((uint)1200, state.Transponder.AssignedCode);
        Assert.Equal(state.Transponder.AssignedCode, state.Transponder.Code);
        Assert.Equal("OAK", state.FlightPlan.Destination);
    }

    private static string BuildSingleAircraftScenario(bool includeFlightPlan)
    {
        var aircraft = new ScenarioAircraft
        {
            Id = "test-ac-1",
            AircraftId = "N123XX",
            AircraftType = "C172",
            TransponderMode = "C",
            StartingConditions = new StartingConditions
            {
                Type = "FixOrFrd",
                Fix = "OAK360010",
                Altitude = 4500,
            },
        };

        if (includeFlightPlan)
        {
            aircraft.FlightPlan = new ScenarioFlightPlan
            {
                Rules = "VFR",
                Departure = "OAK",
                Destination = "OAK",
                CruiseAltitude = 0,
                CruiseSpeed = 100,
            };
        }

        var scenario = new Scenario
        {
            Id = "test-scenario",
            Name = "test",
            ArtccId = "ZOA",
            PrimaryAirportId = "OAK",
            Aircraft = [aircraft],
        };

        return JsonSerializer.Serialize(scenario);
    }
}
