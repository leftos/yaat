using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Phases.Ground;
using Yaat.Sim.Phases.Tower;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// Under hold for release (OAK 28R: the committed SJC layout is a runway outline with no taxi graph) the earliest due runway
/// departure at a field spawns on time at its runway's lineup hold-short bar,
/// held ("Holding short (held)"); the next one spawns only once the bar is clear of it. With no ground layout for the field it
/// spawns lined up, held ("Lined up (held)"). A timed preset CTO waits for the release.
/// </summary>
[Collection("NavDbMutator")]
public class HoldForReleaseBarSpawnE2ETests
{
    public HoldForReleaseBarSpawnE2ETests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void HeldRunwayDeparture_SpawnsOnTimeAtTheBar_AndTheNextWaitsUntilTheBarClears()
    {
        var groundData = new TestAirportGroundData();
        AirportGroundLayout layout = groundData.GetLayout("OAK") ?? throw new InvalidOperationException("no OAK layout");
        GroundNode bar =
            GroundCommandHandler.ResolveRunwayHoldShortNode(layout, new HoldShortTarget("28R", null, false), null)
            ?? throw new InvalidOperationException("no 28R bar");
        SimulationEngine engine = Load(groundData, Scenario("OAK", Ac("SWA1", 50), Ac("SWA2", 300)));
        SimScenarioState scenario = engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "OAK").Success);

        TickTo(engine, 60);

        AircraftState swa1 = engine.FindAircraft("SWA1") ?? throw new InvalidOperationException("SWA1 did not spawn on time");
        HoldingShortPhase holding = Assert.IsType<HoldingShortPhase>(swa1.Phases!.CurrentPhase);
        Assert.Equal(bar.Id, holding.HoldShort.NodeId);
        Assert.True(swa1.IsOnGround);
        Assert.True(swa1.Ground.HeldForRelease);
        HeldDeparture first = HeldReleaseService.BuildRundown(scenario, engine.World).First();
        Assert.Equal("SWA1", first.Callsign);
        Assert.Equal("Holding short (held)", first.Status);
        Assert.Null(engine.FindAircraft("SWA2"));

        TickTo(engine, 320);
        Assert.Null(engine.FindAircraft("SWA2"));

        Assert.True(HeldReleaseService.Release(scenario, engine.World, engine.World.Rng, "SWA1", null).Success);
        CommandResult cto = engine.SendCommand("SWA1", "CTO");
        Assert.True(cto.Success, cto.Message);
        Assert.True(TickUntil(engine, () => swa1.Phases?.CurrentPhase is not HoldingShortPhase, 120) > 0, "SWA1 never left the bar");
        Assert.True(TickUntil(engine, () => engine.FindAircraft("SWA2") is not null, 2) > 0, "SWA2 did not spawn once the bar cleared");

        AircraftState swa2 = engine.FindAircraft("SWA2")!;
        Assert.Equal(bar.Id, Assert.IsType<HoldingShortPhase>(swa2.Phases!.CurrentPhase).HoldShort.NodeId);
        Assert.True(swa2.Ground.HeldForRelease);
    }

    [Fact]
    public void HeldRunwayDeparture_AtAFieldWithNoLayout_SpawnsLinedUpAndHeld()
    {
        var groundData = new TestAirportGroundData();
        Assert.Null(groundData.GetLayout("PAO"));
        SimulationEngine engine = Load(groundData, Scenario("PAO", Ac("SWA1", 50, "KPAO", "31")));
        SimScenarioState scenario = engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "PAO").Success);

        TickTo(engine, 60);

        AircraftState swa1 = engine.FindAircraft("SWA1") ?? throw new InvalidOperationException("SWA1 did not spawn on time");
        Assert.IsType<LinedUpAndWaitingPhase>(swa1.Phases!.CurrentPhase);
        Assert.True(swa1.Ground.HeldForRelease);
        Assert.Equal("Lined up (held)", Assert.Single(HeldReleaseService.BuildRundown(scenario, engine.World)).Status);
    }

    [Fact]
    public void HeldRunwayDeparture_TimedPresetCto_WaitsAtTheBarForTheRelease_ThenDeparts()
    {
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("OAK"));
        string swa1 = Ac("SWA1", 50, """{ "id": "p0", "command": "CTO", "timeOffset": 30 }""");
        SimulationEngine engine = Load(groundData, Scenario("OAK", swa1));
        SimScenarioState scenario = engine.Scenario!;
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "OAK").Success);

        TickTo(engine, 140);

        AircraftState aircraft = engine.FindAircraft("SWA1") ?? throw new InvalidOperationException("SWA1 did not spawn on time");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases!.CurrentPhase);
        Assert.True(aircraft.IsOnGround);
        Assert.Single(scenario.PresetQueue, p => p.Command == "CTO");

        Assert.True(HeldReleaseService.Release(scenario, engine.World, engine.World.Rng, "SWA1", null).Success);
        Assert.True(TickUntil(engine, () => !aircraft.IsOnGround, 240) > 0, "the preset CTO never fired after the release");
    }

    [Fact]
    public void HeldRunwayDeparture_ReleasedAtTheBar_InASoloRoom_LeavesOnItsOwn()
    {
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("OAK"));
        SimulationEngine engine = Load(groundData, Scenario("OAK", Ac("SWA1", 50)));
        var terminal = new List<TerminalEntry>();
        engine.TerminalEntryEmitted += entry => terminal.Add(entry);
        SimScenarioState scenario = engine.Scenario!;
        scenario.SoloTrainingMode = true;
        Assert.True(HeldReleaseService.Arm(scenario, engine.World, "OAK").Success);
        TickTo(engine, 60);
        AircraftState aircraft = engine.FindAircraft("SWA1") ?? throw new InvalidOperationException("SWA1 did not spawn on time");
        Assert.IsType<HoldingShortPhase>(aircraft.Phases!.CurrentPhase);

        Assert.True(HeldReleaseService.Release(scenario, engine.World, engine.World.Rng, "SWA1", null).Success);

        Assert.True(TickUntil(engine, () => !aircraft.IsOnGround, 240) > 0, "the released departure never left the bar on its own");
        Assert.Contains(terminal, t => t.Message.Contains("[HFR] Released", StringComparison.Ordinal));
    }

    private static SimulationEngine Load(TestAirportGroundData groundData, string scenarioJson)
    {
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> warnings = engine.LoadScenario(scenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
        Assert.DoesNotContain(warnings, w => w.Contains("error", StringComparison.OrdinalIgnoreCase));
        engine.TickOneSecond();
        return engine;
    }

    private static void TickTo(SimulationEngine engine, double elapsed)
    {
        while (engine.Scenario!.ElapsedSeconds < elapsed)
        {
            engine.TickOneSecond();
        }
    }

    private static int TickUntil(SimulationEngine engine, Func<bool> done, int maxSeconds)
    {
        for (int i = 1; i <= maxSeconds; i++)
        {
            engine.TickOneSecond();
            if (done())
            {
                return i;
            }
        }

        return -1;
    }

    private static string Ac(string cs, int delay, string presets = "") => Ac(cs, delay, "KOAK", "28R", presets);

    private static string Ac(string cs, int delay, string departure, string runway, string presets = "") =>
        $$"""
            { "id": "{{cs}}", "aircraftId": "{{cs}}", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "OnRunway", "runway": "{{runway}}" },
              "flightplan": { "rules": "IFR", "departure": "{{departure}}", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" },
              "presetCommands": [{{presets}}], "spawnDelay": {{delay}} }
            """;

    private static string Scenario(string primaryAirport, params string[] aircraft) =>
        $$"""
            { "id": "yaat-2937", "name": "HFR bar spawn", "artccId": "ZOA", "primaryAirportId": "{{primaryAirport}}",
              "aircraft": [ {{string.Join(", ", aircraft)}} ] }
            """;
}
