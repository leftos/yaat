using Xunit;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// The run profile is the one enumeration of what a replay may do differently (ADR 0005). These pin the allowance
/// table, the engine's default, the replay driver's scoping of the profile around every stepping entry point, and
/// the two recorders' obedience to it. The tick oracle cannot see any of this — the action log is not snapshot
/// state and its fixtures reach neither generators nor brains — so these are the gate.
/// </summary>
public class RunProfileTests(ITestOutputHelper output)
{
    private const string ScenarioJson = """
        {
          "id": "01TEST00000000000000000002",
          "name": "RunProfileTests",
          "artccId": "ZOA",
          "primaryAirportId": "SFO",
          "aircraft": [],
          "initializationTriggers": [],
          "aircraftGenerators": [
            {
              "id": "gen-profile",
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

    [Theory]
    [InlineData(RunKind.Live, true)]
    [InlineData(RunKind.Test, true)]
    [InlineData(RunKind.Soak, true)]
    [InlineData(RunKind.Replay, false)]
    public void OnlyReplay_WithholdsEveryAllowance(RunKind kind, bool allowed)
    {
        var profile = RunProfile.For(kind);

        Assert.Equal(kind, profile.Kind);
        Assert.Equal(allowed, profile.RecordsActions);
        Assert.Equal(allowed, profile.RunsGenerators);
        Assert.Equal(allowed, profile.RunsControllerAi);
    }

    [Fact]
    public void BareEngine_IsATestRun()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());

        Assert.Same(RunProfile.Test, engine.RunProfile);
    }

    [Fact]
    public void EnterReplay_RestoresThePreviousProfile_EvenWhenNested()
    {
        var engine = new SimulationEngine(new TestAirportGroundData()) { RunProfile = RunProfile.Soak };

        using (engine.EnterReplay())
        {
            Assert.Same(RunProfile.Replay, engine.RunProfile);
            using (engine.EnterReplay())
            {
                Assert.Same(RunProfile.Replay, engine.RunProfile);
            }

            Assert.Same(RunProfile.Replay, engine.RunProfile);
        }

        Assert.Same(RunProfile.Soak, engine.RunProfile);
    }

    public static TheoryData<string> DriverEntryPoints =>
        ["ReplayFromStartTo", "FastForwardTo", "ReplayRange", "ReplayOneSecond", "ReplayOneSubTick"];

    /// <summary>
    /// Every stepping entry point runs its seconds as a replay and hands the engine back in the profile it had —
    /// observed from inside through <see cref="SimulationEngine.TickCompleted"/>, which fires within the scope.
    /// </summary>
    [Theory]
    [MemberData(nameof(DriverEntryPoints))]
    public void EveryDriverEntryPoint_RunsAsReplay_ThenRestoresTheHostProfile(string entryPoint)
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        engine.RunProfile = RunProfile.Soak;
        var observed = new List<RunKind>();
        engine.TickCompleted += _ => observed.Add(engine.RunProfile.Kind);

        switch (entryPoint)
        {
            case "ReplayFromStartTo":
                engine.ReplayFromStartTo(3, []);
                break;
            case "FastForwardTo":
                engine.FastForwardTo(3, []);
                break;
            case "ReplayRange":
                engine.ReplayRange(0, 3, []);
                break;
            case "ReplayOneSecond":
                engine.ArmReplay([]);
                engine.ReplayOneSecond();
                engine.ReplayOneSecond();
                engine.ReplayOneSecond();
                break;
            case "ReplayOneSubTick":
                engine.ArmReplay([]);
                for (int sub = 0; sub < 3 * SimulationEngine.PhysicsSubTickRate; sub++)
                {
                    engine.ReplayOneSubTick();
                }
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(entryPoint), entryPoint, "Unknown driver entry point");
        }

        Assert.Equal([RunKind.Replay, RunKind.Replay, RunKind.Replay], observed);
        Assert.Same(RunProfile.Soak, engine.RunProfile);
    }

    /// <summary>The hybrid test pattern: replay to a cutoff, then tick live from there and expect the live seconds to record again.</summary>
    [Fact]
    public void ReplayToACutoff_ThenTickLive_RecordsAgain()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        engine.ReplayFromStartTo(2, []);
        Assert.Empty(engine.Scenario!.ActionLog);

        engine.TickOneSecond();

        var spawn = Assert.Single(engine.Scenario.ActionLog);
        Assert.IsType<RecordedAircraftSpawn>(spawn);
    }

    [Fact]
    public void Recorders_ObeyRecordsActions()
    {
        var engine = BuildLoadedEngine();
        if (engine is null)
        {
            return;
        }

        engine.TickOneSecond();
        var generated = Assert.Single(engine.World.GetSnapshot());
        engine.Scenario!.ActionLog.Clear();

        engine.RunProfile = RunProfile.Replay;
        engine.RecordAction(new RecordedChat(1, "LF", "not recorded"));
        engine.RecordGeneratedSpawn(generated);
        Assert.Empty(engine.Scenario.ActionLog);

        engine.RunProfile = RunProfile.Live;
        engine.RecordAction(new RecordedChat(1, "LF", "recorded"));
        engine.RecordGeneratedSpawn(generated);
        Assert.Equal(2, engine.Scenario.ActionLog.Count);
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
