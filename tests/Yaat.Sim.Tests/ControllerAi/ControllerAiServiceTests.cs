using System.Text.Json;
using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>The service's tick contract, the engine's replay/playback guard, and the config's snapshot round trip.</summary>
public class ControllerAiServiceTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ControllerAiServiceTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void Tick_PublishesStaffing_TurnsRejectionsIntoAnomalies_AndTicksBrainsInRankOrder()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(AiTestHost.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc);
        var scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        var order = new List<string>();
        var towerProbe = new ProbeBrain(tower, order);
        var groundProbe = new ProbeBrain(ground, order);
        var sink = new EngineAiCommandSink(engine);
        var config = new ControllerAiConfig
        {
            Seed = 3,
            EnabledPositionIds = [tower.PositionId, ground.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
        };
        var service = new AiControllerService([towerProbe, groundProbe], new HeadlessAiStaffing([tower, ground], scenario), sink, config);
        scenario.ControllerAi = config;
        engine.ControllerAi = service;

        sink.Issue(new AiCommandRequest(tower, AiTestHost.Callsign, "CTO", new AiIntent("probe", "")));
        AiTestHost.Tick(engine, 1);

        Assert.Equal(["OAK_GND", "OAK_TWR"], order);
        // Published staffing is sorted by position id (the roster's order), not by role rank.
        Assert.Equal(
            new[] { tower.PositionId, ground.PositionId }.OrderBy(id => id, StringComparer.Ordinal),
            scenario.AiStaffedPositions.Select(p => p.PositionId)
        );
        Assert.True(scenario.PilotContacts.AnyAnswering);
        var rejected = Assert.Single(scenario.AiAnomalies.Drain());
        Assert.Equal(AiAnomalyKind.CommandRejected, rejected.Kind);
        Assert.Equal(tower.PositionId, rejected.PositionId);
        Assert.StartsWith("CTO:", rejected.Detail, StringComparison.Ordinal);
        Assert.Equal(1, service.TickCount);
    }

    [Fact]
    public void TickControllerAi_IsAGuard_NotAScheduler()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(AiTestHost.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc);
        var scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        var order = new List<string>();
        var config = new ControllerAiConfig
        {
            Seed = 3,
            EnabledPositionIds = [ground.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
        };
        engine.ControllerAi = new AiControllerService(
            [new ProbeBrain(ground, order)],
            new HeadlessAiStaffing([ground], scenario),
            new EngineAiCommandSink(engine),
            config
        );

        // No config on the scenario: the AI is off.
        engine.TickControllerAi();
        Assert.Empty(order);

        scenario.ControllerAi = config;
        engine.TickControllerAi();
        Assert.Single(order);

        // Tape playback never runs the brains.
        scenario.IsPlaybackMode = true;
        engine.TickControllerAi();
        Assert.Single(order);
    }

    [Fact]
    public void SoloStudentOnAnAiPosition_SuspendsThatBrain()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var tower = TestAiPositions.OakTower(_zoa);
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(AiTestHost.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc);
        var scenario = engine.Scenario!;
        scenario.ArtccConfig = _zoa;
        scenario.SoloTrainingMode = true;
        scenario.StudentPosition = tower.Identity;
        scenario.StudentPositionType = "TWR";
        var order = new List<string>();
        var config = new ControllerAiConfig
        {
            Seed = 3,
            EnabledPositionIds = [ground.PositionId, tower.PositionId],
            RoleOverrides = AiTestHost.NoOverrides,
        };
        scenario.ControllerAi = config;
        engine.ControllerAi = new AiControllerService(
            [new ProbeBrain(ground, order), new ProbeBrain(tower, order)],
            new HeadlessAiStaffing([ground, tower], scenario),
            new EngineAiCommandSink(engine),
            config
        );

        engine.TickControllerAi();

        Assert.Equal(["OAK_GND"], order);
        Assert.Equal(["OAK_GND"], scenario.AiStaffedPositions.Select(p => p.Callsign));
    }

    [Fact]
    public void Config_RoundTripsThroughTheScenarioSnapshot_AndIsNullInPreFeatureSnapshots()
    {
        if (_zoa is null)
        {
            return;
        }

        var delivery = _zoa.FindPositionByCallsign("OAK_DEL")!.Id;
        var ground = _zoa.FindPositionByCallsign("OAK_GND")!.Id;
        var engine = new SimulationEngine(new TestAirportGroundData());
        engine.LoadScenario(AiTestHost.ParkedAtOak, 7, MagneticDeclination.EvaluationDateUtc);
        var scenario = engine.Scenario!;
        scenario.ControllerAi = new ControllerAiConfig
        {
            Seed = 99,
            EnabledPositionIds = [ground, delivery],
            RoleOverrides = new Dictionary<string, ControlRole>(StringComparer.Ordinal) { [delivery] = ControlRole.Ground },
        };
        scenario.AiAnomalies.Open(AiAnomalyKind.StuckAircraft, ground, "N1", 1, "");

        var json = JsonSerializer.Serialize(engine.CaptureSnapshot(-1), RecordingJsonOptions.Default);
        var restored = JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
        scenario.ControllerAi = null;
        engine.RestoreFromSnapshot(restored);

        Assert.NotNull(scenario.ControllerAi);
        Assert.Equal(99, scenario.ControllerAi.Seed);
        Assert.Equal([ground, delivery], scenario.ControllerAi.EnabledPositionIds);
        Assert.Equal(ControlRole.Ground, scenario.ControllerAi.RoleOverrides[delivery]);
        Assert.Equal(0, scenario.AiAnomalies.OpenCount);

        var preFeature = JsonSerializer.Deserialize<StateSnapshotDto>(
            json.Replace("\"ControllerAi\"", "\"ControllerAiRemoved\"", StringComparison.Ordinal),
            RecordingJsonOptions.Default
        )!;
        Assert.Null(preFeature.Scenario.ControllerAi);
    }

    private sealed class ProbeBrain(AiPositionConfig position, List<string> order) : IPositionBrain
    {
        public AiPositionConfig Position => position;

        public void Tick(AiTickContext context) => order.Add(position.Callsign);

        public void Reset() { }
    }
}
