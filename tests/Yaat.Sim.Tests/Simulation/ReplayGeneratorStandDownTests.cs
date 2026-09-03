using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// In a replay the recorded spawns are the traffic: the runtime generators must stand down whether or not the
/// action log happens to carry a <see cref="RecordedAircraftSpawn"/>. The stand-down used to be conditional on
/// that — a compatibility shim for recordings that predate recorded spawns — so a recording whose generators
/// never fired replayed with live generators inventing aircraft the session never had.
/// </summary>
public class ReplayGeneratorStandDownTests(ITestOutputHelper output)
{
    private const int Seconds = 30;

    private const string ScenarioJson = """
        {
          "id": "01TEST00000000000000000001",
          "name": "ReplayGeneratorStandDownTests",
          "artccId": "ZOA",
          "primaryAirportId": "SFO",
          "aircraft": [],
          "initializationTriggers": [],
          "aircraftGenerators": [
            {
              "id": "gen-standdown",
              "runway": "28R",
              "engineType": "Jet",
              "weightCategory": "Large",
              "initialDistance": 10,
              "maxDistance": 50,
              "intervalDistance": 5,
              "startTimeOffset": 0,
              "maxTime": 3600,
              "intervalTime": 300,
              "randomizeInterval": false,
              "randomizeWeightCategory": false
            }
          ]
        }
        """;

    [Fact]
    public void Replay_WithoutRecordedSpawns_DoesNotRunGenerators()
    {
        var live = BuildLoadedEngine();
        if (live is null)
        {
            return;
        }

        var actions = RunLive(live);
        var stripped = actions.Where(static a => a is not RecordedAircraftSpawn).ToList();

        var replay = BuildLoadedEngine()!;
        replay.ArmReplay(stripped);
        for (int t = 0; t < Seconds; t++)
        {
            replay.ReplayOneSecond();
        }

        var callsigns = replay.World.GetSnapshot().Select(static a => a.Callsign).ToList();
        output.WriteLine($"replayed world without recorded spawns: [{string.Join(", ", callsigns)}]");
        Assert.Empty(callsigns);
    }

    [Fact]
    public void Replay_WithRecordedSpawns_ReproducesThemAndRecordsNothing()
    {
        var live = BuildLoadedEngine();
        if (live is null)
        {
            return;
        }

        var actions = RunLive(live);
        var recordedSpawns = actions.OfType<RecordedAircraftSpawn>().Select(static s => s.Aircraft.Callsign).OrderBy(static c => c).ToList();

        var replay = BuildLoadedEngine()!;
        replay.ArmReplay(actions);
        for (int t = 0; t < Seconds; t++)
        {
            replay.ReplayOneSecond();
        }

        var callsigns = replay.World.GetSnapshot().Select(static a => a.Callsign).OrderBy(static c => c).ToList();
        Assert.Equal(recordedSpawns, callsigns);
        Assert.Empty(replay.Scenario!.ActionLog);
    }

    /// <summary>Ticks the live engine and returns its action log, which must hold at least one generator spawn.</summary>
    private static List<RecordedAction> RunLive(SimulationEngine live)
    {
        for (int t = 0; t < Seconds; t++)
        {
            live.TickOneSecond();
        }

        var actions = live.Scenario!.ActionLog.ToList();
        Assert.Contains(actions, static a => a is RecordedAircraftSpawn);
        return actions;
    }

    private SimulationEngine? BuildLoadedEngine()
    {
        TestVnasData.EnsureInitialized();
        if (TestVnasData.NavigationDb is null)
        {
            return null;
        }

        var groundData = new TestAirportGroundData();
        if (groundData.GetLayout("SFO") is null)
        {
            return null;
        }

        var engine = new SimulationEngine(groundData);
        var warnings = engine.LoadScenario(ScenarioJson, rngSeed: 42, magneticModelDateUtc: MagneticDeclination.EvaluationDateUtc);
        foreach (var w in warnings)
        {
            output.WriteLine($"[load-warn] {w}");
        }

        return engine;
    }
}
