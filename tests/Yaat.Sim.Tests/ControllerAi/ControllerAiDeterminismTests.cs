using System.Text.Json;
using Xunit;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.ControllerAi;

/// <summary>
/// The permanent CA0 acceptance test: the same scenario and seed with AI Ground + AI Local observing produce
/// byte-identical action logs, anomaly streams and final snapshots; a different seed produces a different world.
/// </summary>
public class ControllerAiDeterminismTests
{
    private const string OakScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";
    private const int Seconds = 900;
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ControllerAiDeterminismTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void SameSeed_SameActionsAnomaliesAndSnapshot_DifferentSeedDiffers()
    {
        if (_zoa is null || !File.Exists(OakScenarioPath))
        {
            return;
        }

        var json = File.ReadAllText(OakScenarioPath);
        var first = Run(json, 42);
        var second = Run(json, 42);
        var other = Run(json, 43);

        Assert.Equal(first.Actions, second.Actions);
        Assert.Equal(first.Anomalies, second.Anomalies);
        Assert.Equal(first.Snapshot, second.Snapshot);
        Assert.NotEqual(first.Snapshot, other.Snapshot);
    }

    private (string Actions, string Anomalies, string Snapshot) Run(string scenarioJson, int seed)
    {
        var positions = new[] { TestAiPositions.OakGround(_zoa!), TestAiPositions.OakTower(_zoa!) };
        var engine = AiTestHost.Load(scenarioJson, _zoa!, seed, positions);
        var anomalies = new List<AiAnomalyEvent>();
        for (int t = 0; t < Seconds; t++)
        {
            AiTestHost.Tick(engine, 1);
            anomalies.AddRange(engine.Scenario!.AiAnomalies.Drain());
        }

        var scenario = engine.Scenario!;
        return (
            JsonSerializer.Serialize(scenario.ActionLog, RecordingJsonOptions.Default),
            JsonSerializer.Serialize(anomalies, RecordingJsonOptions.Default),
            JsonSerializer.Serialize(engine.CaptureSnapshot(scenario.ActionLog.Count - 1), RecordingJsonOptions.Default)
        );
    }
}
