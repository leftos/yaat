using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport.Precompute;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation;

/// <summary>
/// A snapshot restore gives back the delayed queue as it was captured: each queued spawn keeps the phases the scenario
/// loader built for it (an OnFinal arrival's final approach) and its spawn metadata, through the JSON a recording stores.
/// A schema-53 snapshot, whose queue holds only each aircraft's raw <c>LoadedAircraft</c> JSON, still loads as it did.
/// </summary>
public class DelayedQueueSnapshotRestoreTests
{
    private const string ScenarioPath = "TestData/sfo-gc-scenario.json";

    [Fact]
    public void RestoreFromSnapshot_KeepsEachQueuedSpawnsPhasesAndMetadata()
    {
        SimulationEngine engine = LoadSfoGc();
        List<string> before = Describe(engine.Scenario!);
        Assert.Contains(engine.Scenario!.DelayedQueue, d => d.Aircraft.State.Phases is { Phases.Count: > 0 });

        StateSnapshotDto capture = engine.CaptureSnapshot();
        engine.RestoreFromSnapshot(
            JsonSerializer.Deserialize<StateSnapshotDto>(
                JsonSerializer.Serialize(capture, RecordingJsonOptions.Default),
                RecordingJsonOptions.Default
            )!
        );

        Assert.Equal(before, Describe(engine.Scenario!));
    }

    [Fact]
    public void RestoreFromSnapshot_Schema53RawJsonQueue_LoadsWithoutPhases()
    {
        SimulationEngine engine = LoadSfoGc();
        List<DelayedSpawn> queue = engine.Scenario!.DelayedQueue;
        Assert.Contains(queue, d => d.Aircraft.PresetCommands.Count > 0);
        List<string> before = [.. queue.Select(DescribeLegacy)];

        engine.RestoreFromSnapshot(Schema53Snapshot(engine.CaptureSnapshot(), queue));

        Assert.Equal(before, [.. engine.Scenario!.DelayedQueue.Select(DescribeLegacy)]);
        Assert.All(
            engine.Scenario!.DelayedQueue,
            d => Assert.True(d.Aircraft.State.Phases is null or { Phases.Count: 0 }, $"{d.Aircraft.State.Callsign} came back with phases")
        );
    }

    private static SimulationEngine LoadSfoGc()
    {
        TestVnasData.EnsureInitialized();
        Assert.NotNull(TestVnasData.NavigationDb);
        var groundData = new TestAirportGroundData();
        Assert.NotNull(groundData.GetLayout("SFO"));
        var engine = new SimulationEngine(groundData, PushPlanStore.CreateShipped());
        engine.LoadScenario(File.ReadAllText(ScenarioPath), rngSeed: 2205, sessionStartUtc: MagneticDeclination.EvaluationDateUtc);
        engine.TickOneSecond();
        return engine;
    }

    /// <summary>The capture rewritten as schema 53 wrote it: each queued spawn as its serialized <c>LoadedAircraft</c> only.</summary>
    private static StateSnapshotDto Schema53Snapshot(StateSnapshotDto capture, List<DelayedSpawn> queue)
    {
        JsonObject node = JsonSerializer.SerializeToNode(capture, RecordingJsonOptions.Default)!.AsObject();
        node[nameof(StateSnapshotDto.SchemaVersion)] = 53;
        node[nameof(StateSnapshotDto.Scenario)]![nameof(ScenarioSnapshotDto.DelayedQueue)] = new JsonArray([
            .. queue.Select(d =>
                JsonSerializer.SerializeToNode(
                    new DelayedSpawnDto
                    {
                        AircraftJson = JsonSerializer.Serialize(d.Aircraft),
                        SpawnAtSeconds = d.SpawnAtSeconds,
                        HeldForRelease = d.HeldForRelease,
                    },
                    RecordingJsonOptions.Default
                )
            ),
        ]);
        StateSnapshotDto legacy = node.Deserialize<StateSnapshotDto>(RecordingJsonOptions.Default)!;
        Assert.Equal(53, legacy.SchemaVersion);
        Assert.All(legacy.Scenario.DelayedQueue!, d => Assert.Null(d.Aircraft));
        return legacy;
    }

    private static string DescribeLegacy(DelayedSpawn d) =>
        $"{d.Aircraft.State.Callsign} at={d.SpawnAtSeconds} presets={string.Join(";", d.Aircraft.PresetCommands.Select(p => $"{p.Id}|{p.Command}|{p.TimeOffset}"))}";

    /// <summary>One line per queued spawn: its timing, its phase list (index and types) and its loader metadata.</summary>
    private static List<string> Describe(SimScenarioState scenario) =>
        [
            .. scenario.DelayedQueue.Select(d =>
            {
                LoadedAircraft a = d.Aircraft;
                string phases = a.State.Phases is { } list
                    ? $"{list.CurrentIndex}:{string.Join(",", list.Phases.Select(p => p.GetType().Name))}"
                    : "none";
                string presets = string.Join(";", a.PresetCommands.Select(p => $"{p.Id}|{p.Command}|{p.TimeOffset}"));
                AutoTrackConditions? track = a.AutoTrackConditions;
                string autoTrack = track is null
                    ? "none"
                    : $"{track.PositionId}|{track.HandoffDelay}|{track.ScratchPad}|{track.InterimAltitude}|{track.ClearedAltitude}";
                return $"{a.State.Callsign} at={d.SpawnAtSeconds} held={d.HeldForRelease} delay={a.SpawnDelaySeconds} "
                    + $"deferral={a.DeferralReason} phases={phases} presets={presets} autoTrack={autoTrack} "
                    + $"messages={string.Join(";", a.AutoTrackMessages)}";
            }),
        ];
}
