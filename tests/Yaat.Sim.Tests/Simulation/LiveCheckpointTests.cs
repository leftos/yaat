using System.Text.Json;
using Xunit;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Oracle;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A live checkpoint holds a captured state compressed and gives back the same state: what a rewind restores from it is
/// exactly what the room had when it was captured.
/// </summary>
public class LiveCheckpointTests
{
    private const string ScenarioPath = "TestData/issue153-s2-oak-5-2-scenario.json";

    [Fact]
    public void RoundTrip_RestoresTheSameSnapshot()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(TestVnasData.NavigationDb);
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("OAK"));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 42, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        for (int i = 0; i < 30; i++)
        {
            engine.TickOneSecond();
        }

        StateSnapshotDto state = engine.CaptureSnapshot();
        Assert.True(state.Aircraft.Count > 2, $"only {state.Aircraft.Count} aircraft in the capture");

        var checkpoint = LiveCheckpoint.Capture(state, 30, 7);
        var restored = checkpoint.ToTimedSnapshot();

        Assert.Equal(30, restored.ElapsedSeconds);
        Assert.Equal(7, restored.ActionIndex);
        IReadOnlyList<SnapshotDivergence> divergences = SnapshotTreeDiff.Compare(state, restored.State);
        Assert.True(divergences.Count == 0, string.Join("\n", divergences.Take(20).Select(d => $"{d.Path}: {d.Left} | {d.Right}")));
        int uncompressed = JsonSerializer.SerializeToUtf8Bytes(state, RecordingJsonOptions.Default).Length;
        Assert.True(checkpoint.BrotliJson.Length < uncompressed, $"{checkpoint.BrotliJson.Length} compressed vs {uncompressed} uncompressed");
    }
}
