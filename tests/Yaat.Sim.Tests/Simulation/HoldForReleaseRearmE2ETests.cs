using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Hold for release disarmed at a field releases only the queued runway spawns already due there: a spawn not yet due keeps
/// its candidacy, so a later arm at the same field still holds it: it spawns on its scheduled time held (lined up, since the
/// committed SJC layout has no taxi graph and so no hold-short bar).
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
        Assert.True(HeldReleaseService.Disarm(scenario, engine.World, "SJC", null).Success);
        Tick(engine, 2);
        AircraftState swa1 = engine.FindAircraft("SWA1") ?? throw new InvalidOperationException("SWA1 left the world");
        Assert.False(swa1.Ground.HeldForRelease); // SWA1, held on the runway, is released at the HFROFF
        Assert.True(engine.SendCommand("SWA1", "CTO").Success); // an RPO room waits for the CTO; it clears the runway
        Tick(engine, 20);
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "SJC").Success);

        Tick(engine, 220); // t ~ 343, past SWA2's scheduled 300

        AircraftState swa2 = engine.FindAircraft("SWA2") ?? throw new InvalidOperationException("SWA2 did not spawn at its time");
        Assert.IsType<LinedUpAndWaitingPhase>(swa2.Phases!.CurrentPhase);
        Assert.True(swa2.Ground.HeldForRelease);
        Assert.Empty(scenario.DelayedQueue);
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
