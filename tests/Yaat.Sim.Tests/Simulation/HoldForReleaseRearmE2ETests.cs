using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Hold for release disarmed at a field releases only the queued runway spawns already due there: a spawn not yet due keeps
/// its candidacy, so a later arm at the same field still holds it and it never appears on its scheduled time.
/// </summary>
[Collection("NavDbMutator")]
public class HoldForReleaseRearmE2ETests
{
    public HoldForReleaseRearmE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void HfrOffThenHfr_HoldsARunwaySpawnThatWasNotYetDueAtTheHfrOff()
    {
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("SJC"));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> warnings = engine.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        SimScenarioState scenario = engine.Scenario!;
        engine.TickOneSecond();
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "SJC").Success);
        Tick(engine, 100); // SWA1 (due 50) held; SWA2 (due 300) not yet due
        Assert.True(HeldReleaseService.Disarm(scenario, engine.World, "SJC").Success);
        Tick(engine, 2);
        Assert.NotNull(engine.FindAircraft("SWA1")); // the due departure is released, as today
        Tick(engine, 20);
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "SJC").Success);

        Tick(engine, 220); // t ~ 343, past SWA2's scheduled 300

        Assert.Null(engine.FindAircraft("SWA2"));
        Assert.True(HeldReleaseService.IsSpawnHeld(scenario, Assert.Single(scenario.DelayedQueue)));
        Assert.Contains(HeldReleaseService.BuildRundown(scenario, engine.World), h => h.Callsign == "SWA2");
    }

    private static void Tick(SimulationEngine engine, int seconds)
    {
        for (int i = 0; i < seconds; i++)
        {
            engine.TickOneSecond();
        }
    }

    private static string Ac(string cs, int delay) =>
        $$"""
            { "id": "{{cs}}", "aircraftId": "{{cs}}", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "OnRunway", "runway": "30R" },
              "flightplan": { "rules": "IFR", "departure": "KSJC", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" },
              "presetCommands": [], "spawnDelay": {{delay}} }
            """;

    private static readonly string ScenarioJson = $$"""
        { "id": "yaat-2936", "name": "HFR re-arm", "artccId": "ZOA", "primaryAirportId": "SJC",
          "aircraft": [ {{Ac("SWA1", 50)}}, {{Ac("SWA2", 300)}} ] }
        """;
}
