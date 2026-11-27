using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// HFROFF at a field whose held runway departures have waited: the departure held at the bar is released at the HFROFF time,
/// and the queued departures' spawn times are rewritten. Keep spacing (<c>HFROFF OAK</c>) moves every departure already due by
/// the hold's length and lets a later one keep its time once the gap before it, never under
/// <see cref="HeldReleaseService.MinimumSpacingSeconds"/>, absorbs the shift; catch-up (<c>HFROFF OAK 2</c>) releases one every
/// two minutes until they are back on schedule. OAK 28R stands in for the field: its committed layout has a taxi graph.
/// </summary>
[Collection("NavDbMutator")]
public class HoldForReleaseHfroffSpacingTests
{
    public HoldForReleaseHfroffSpacingTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void KeepSpacing_ShortHold_ReleasesTheBarAndLeavesTheLaterOnesOnTime()
    {
        SimulationEngine engine = LoadArmed();
        TickTo(engine, 130);

        Assert.True(HeldReleaseService.Disarm(engine.Scenario!, engine.World, "OAK", null).Success);

        AssertReleasedAtTheBar(engine, "SWA1", 130);
        AssertSpawnTimes(engine, ("SWA2", 300), ("SWA3", 600), ("SWA4", 900), ("SWA5", 1200));
    }

    [Fact]
    public void KeepSpacing_LongHold_ShiftsTheDueOnes_AndSqueezesTheLaterOnesToTwoMinutes()
    {
        SimulationEngine engine = LoadArmed();
        TickTo(engine, 730);

        Assert.True(HeldReleaseService.Disarm(engine.Scenario!, engine.World, "OAK", null).Success);

        AssertReleasedAtTheBar(engine, "SWA1", 730);
        AssertSpawnTimes(engine, ("SWA2", 1020), ("SWA3", 1320), ("SWA4", 1440), ("SWA5", 1560));
        AssertReleaseFlags(engine, released: ["SWA2", "SWA3"], later: ["SWA4", "SWA5"]);
    }

    [Fact]
    public void CatchUp_TwoMinutes_ReleasesOneEveryTwoMinutesUntilBackOnSchedule()
    {
        SimulationEngine engine = LoadArmed();
        TickTo(engine, 730);

        Assert.True(HeldReleaseService.Disarm(engine.Scenario!, engine.World, "OAK", 120).Success);

        AssertReleasedAtTheBar(engine, "SWA1", 730);
        AssertSpawnTimes(engine, ("SWA2", 850), ("SWA3", 970), ("SWA4", 1090), ("SWA5", 1210));
        AssertReleaseFlags(engine, released: ["SWA2", "SWA3"], later: ["SWA4", "SWA5"]);
    }

    [Fact]
    public void CatchUp_ThroughTheCommandPath_ReschedulesAsTheService()
    {
        SimulationEngine engine = LoadArmed();
        TickTo(engine, 730);

        CommandResult off = engine.SendCommand("", "HFROFF OAK 2");

        Assert.True(off.Success, off.Message);
        Assert.False(HeldReleaseService.IsAirportArmed(engine.Scenario!, "OAK"));
        AssertReleasedAtTheBar(engine, "SWA1", 730);
        AssertSpawnTimes(engine, ("SWA2", 850), ("SWA3", 970), ("SWA4", 1090), ("SWA5", 1210));
    }

    [Fact]
    public void KeepSpacing_NoHeldDepartureAtTheBar_ShiftsFromTheFirstQueuedOne()
    {
        SimulationEngine engine = LoadArmed();
        TickTo(engine, 100);
        Assert.True(HeldReleaseService.Release(engine.Scenario!, engine.World, engine.World.Rng, "SWA1", null).Success);
        TickTo(engine, 730);
        Assert.False(engine.FindAircraft("SWA1")!.Ground.HeldForRelease);
        Assert.Null(engine.FindAircraft("SWA2"));

        Assert.True(HeldReleaseService.Disarm(engine.Scenario!, engine.World, "OAK", null).Success);

        AssertSpawnTimes(engine, ("SWA2", 730), ("SWA3", 1030), ("SWA4", 1150), ("SWA5", 1270));
        AssertReleaseFlags(engine, released: ["SWA2", "SWA3"], later: ["SWA4", "SWA5"]);
    }

    [Fact]
    public void CatchUp_NotArmed_FailsAndChangesNothing()
    {
        SimulationEngine engine = Load();
        TickTo(engine, 730);
        List<(string, int)> before = [.. engine.Scenario!.DelayedQueue.Select(d => (d.Aircraft.State.Callsign, d.SpawnAtSeconds))];

        HeldReleaseResult result = HeldReleaseService.Disarm(engine.Scenario!, engine.World, "OAK", 120);

        Assert.False(result.Success);
        Assert.Contains("not armed", result.Message, StringComparison.Ordinal);
        Assert.Equal(before, [.. engine.Scenario!.DelayedQueue.Select(d => (d.Aircraft.State.Callsign, d.SpawnAtSeconds))]);
    }

    private static void AssertReleasedAtTheBar(SimulationEngine engine, string callsign, double releasedAt)
    {
        AircraftState aircraft = engine.FindAircraft(callsign) ?? throw new InvalidOperationException($"{callsign} is not in the world");
        Assert.False(aircraft.Ground.HeldForRelease);
        Assert.True(aircraft.Ground.ReleasedForDeparture);
        Assert.Equal(releasedAt, aircraft.Ground.ReleasedAtSeconds);
    }

    /// <summary>
    /// The released set leaves the hold through the spawn gate (no longer a hold candidate, its release in hand); the later set
    /// keeps its candidacy and has no release.
    /// </summary>
    private static void AssertReleaseFlags(SimulationEngine engine, string[] released, string[] later)
    {
        foreach (string callsign in released)
        {
            DelayedSpawn entry = Queued(engine, callsign);
            Assert.False(entry.HeldForRelease, $"{callsign} is still a hold candidate");
            Assert.True(entry.Aircraft.State.Ground.ReleasedAtSpawnGate, $"{callsign} has no release");
        }

        foreach (string callsign in later)
        {
            DelayedSpawn entry = Queued(engine, callsign);
            Assert.True(entry.HeldForRelease, $"{callsign} lost its hold candidacy");
            Assert.False(entry.Aircraft.State.Ground.ReleasedAtSpawnGate, $"{callsign} was released");
        }
    }

    private static DelayedSpawn Queued(SimulationEngine engine, string callsign) =>
        engine.Scenario!.DelayedQueue.Single(d => d.Aircraft.State.Callsign == callsign);

    private static void AssertSpawnTimes(SimulationEngine engine, params (string Callsign, int SpawnAt)[] expected)
    {
        List<(string, int)> actual =
        [
            .. engine.Scenario!.DelayedQueue.OrderBy(d => d.SpawnAtSeconds).Select(d => (d.Aircraft.State.Callsign, d.SpawnAtSeconds)),
        ];
        Assert.Equal([.. expected.Select(e => (e.Callsign, e.SpawnAt))], actual);
    }

    private static SimulationEngine LoadArmed()
    {
        SimulationEngine engine = Load();
        Assert.True(HeldReleaseService.Arm(engine.Scenario!, engine.World, "OAK").Success);
        return engine;
    }

    private static SimulationEngine Load()
    {
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("OAK"));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        List<string> warnings = engine.LoadScenario(ScenarioJson, 42, MagneticDeclination.EvaluationDateUtc);
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

    private static string Ac(string cs, int delay) =>
        $$"""
            { "id": "{{cs}}", "aircraftId": "{{cs}}", "aircraftType": "B738", "transponderMode": "C",
              "startingConditions": { "type": "OnRunway", "runway": "28R" },
              "flightplan": { "rules": "IFR", "departure": "KOAK", "destination": "KLAX", "cruiseAltitude": 35000, "cruiseSpeed": 450, "route": "", "remarks": "", "aircraftType": "B738" },
              "presetCommands": [], "spawnDelay": {{delay}} }
            """;

    private static readonly string ScenarioJson = $$"""
        { "id": "yaat-2937-hfroff", "name": "HFROFF spacing", "artccId": "ZOA", "primaryAirportId": "OAK",
          "aircraft": [ {{Ac("SWA1", 10)}}, {{Ac("SWA2", 300)}}, {{Ac("SWA3", 600)}}, {{Ac("SWA4", 900)}}, {{Ac("SWA5", 1200)}} ] }
        """;
}
