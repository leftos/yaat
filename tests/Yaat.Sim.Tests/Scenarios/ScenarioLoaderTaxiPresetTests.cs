using Xunit;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Scenarios;

/// <summary>
/// The loader arms each parking spawn's initial call-up plan from its timed presets (YAAT-308), and the scenario's
/// parking-call-up source flag (the pacing slider's gate) is true only when some loaded aircraft may make an initial call.
/// </summary>
[Collection("NavDbMutator")]
public class ScenarioLoaderTaxiPresetTests
{
    public ScenarioLoaderTaxiPresetTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static string OakParkingScenario(params (string Callsign, string Stand, string[] Presets)[] aircraft)
    {
        IEnumerable<string> entries = aircraft.Select(ac =>
        {
            string presets = string.Join(", ", ac.Presets.Select((p, i) => $$"""{ "id": "p{{i}}", "command": "{{p}}", "timeOffset": 0 }"""));
            return $$"""
                {
                  "id": "{{ac.Callsign}}",
                  "aircraftId": "{{ac.Callsign}}",
                  "aircraftType": "C172",
                  "startingConditions": { "type": "Parking", "parking": "{{ac.Stand}}" },
                  "presetCommands": [ {{presets}} ]
                }
                """;
        });
        return $$"""{ "id": "test", "name": "Test", "primaryAirportId": "OAK", "aircraft": [ {{string.Join(", ", entries)}} ] }""";
    }

    private static ScenarioLoadResult Load(string json) =>
        ScenarioLoader.Load(json, new TestAirportGroundData(), new Random(0), MagneticDeclination.EvaluationDateUtc);

    [Fact]
    public void ParkingSpawnWithWaitPrefixedTaxiToASpot_AfterTaxiArrival()
    {
        // Inventory finding 1: a TAXI behind a WAIT was missed by the first-word check.
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", ["WAIT 30 TAXI M4 M1 $1"])));

        Assert.Equal(InitialCallupPlan.AfterTaxiArrival, Assert.Single(result.ImmediateAircraft).State.Ground.InitialCallup);
    }

    [Fact]
    public void ParkingSpawnWithNoPresets_StandCall()
    {
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", [])));

        AircraftState state = Assert.Single(result.ImmediateAircraft).State;
        Assert.Equal(InitialCallupPlan.StandCall, state.Ground.InitialCallup);
        Assert.Null(state.Ground.SpawnTaxiway);
    }

    [Fact]
    public void HasParkingSpawns_OnlyGroundSpawnPushes_IsTrue()
    {
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", ["PUSH Z"])));

        Assert.True(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_EveryGroundSpawnPlansNone_IsFalse()
    {
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", ["TAXI W @GA8"]), ("N222", "GA8", ["TAXI W @GA7"])));

        Assert.All(result.ImmediateAircraft, loaded => Assert.Equal(InitialCallupPlan.None, loaded.State.Ground.InitialCallup));
        Assert.False(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_NoPresets_IsTrue()
    {
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", [])));

        Assert.True(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_MixedPlans_IsTrue()
    {
        // A single aircraft that may call is enough for the slider to remain available.
        ScenarioLoadResult result = Load(OakParkingScenario(("N111", "GA7", ["TAXI W @GA8"]), ("N222", "GA8", [])));

        Assert.True(result.HasParkingSpawns);
    }

    [Fact]
    public void HasParkingSpawns_ParkingWithoutGroundData_IsFalse()
    {
        // A parking spawn the loader cannot place is deferred with no plan.
        ScenarioLoadResult result = ScenarioLoader.Load(
            OakParkingScenario(("N111", "GA7", [])),
            groundData: null,
            new Random(0),
            MagneticDeclination.EvaluationDateUtc
        );

        Assert.False(result.HasParkingSpawns);
    }
}
