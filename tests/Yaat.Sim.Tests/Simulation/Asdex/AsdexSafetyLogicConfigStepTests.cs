using System.Text.Json;
using Xunit;
using Yaat.Sim.Asdex;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Asdex;

/// <summary>
/// The CRC-sourced ASDE-X safety-logic configuration on the bare engine: a
/// <see cref="RecordedAsdexSafetyLogicChange"/> carries the Sim-native <see cref="AsdexSafetyLogicConfig"/> and
/// <see cref="SimulationEngine.ApplyRecordedAsdexSafetyLogic"/> writes it onto the scenario, where it is snapshotted.
/// Every run kind therefore carries it: a replay and a reconstruction end where the live run did, and a rewind onto a
/// snapshot that predates a push rebuilds the configuration rather than keeping the live room's.
/// </summary>
public class AsdexSafetyLogicConfigStepTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public AsdexSafetyLogicConfigStepTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine() => _zoa is null ? null : AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);

    private static AsdexSafetyLogicConfig Config(bool isClosed) =>
        new(
            [
                new AsdexRunwayConfig(
                    "28R",
                    [new LatLon(37.7213, -122.2208), new LatLon(37.7222, -122.2101), new LatLon(37.7190, -122.2095)],
                    isClosed
                ),
            ],
            "WEST-PLAN",
            ["OAK_TWR", "OAK_GND"]
        );

    private static void AssertMatches(AsdexSafetyLogicConfig expected, AsdexSafetyLogicConfig? actual)
    {
        Assert.NotNull(actual);
        Assert.Equal(expected.RunwayConfigurationId, actual.RunwayConfigurationId);
        Assert.Equal(expected.InhibitedArrivalAlertPositionIds, actual.InhibitedArrivalAlertPositionIds);

        AsdexRunwayConfig expectedRunway = Assert.Single(expected.Runways);
        AsdexRunwayConfig actualRunway = Assert.Single(actual.Runways);
        Assert.Equal(expectedRunway.Id, actualRunway.Id);
        Assert.Equal(expectedRunway.IsClosed, actualRunway.IsClosed);
        Assert.Equal(expectedRunway.AreaPoints, actualRunway.AreaPoints);
    }

    [Fact]
    public void Apply_WritesTheConfigOntoScenarioState()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AsdexSafetyLogicConfig config = Config(isClosed: true);

        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", config));

        Assert.Same(config, engine.Scenario!.AsdexSafetyLogicConfig);
    }

    [Fact]
    public void Apply_WithNoScenario_IsANoOp()
    {
        var engine = new SimulationEngine(new TestAirportGroundData());
        Assert.Null(engine.Scenario);

        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Config(isClosed: false)));

        // The push is dropped, not stashed: the scenario loaded afterwards starts with no configuration rather than
        // picking up the one that arrived while there was nowhere to put it.
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 7, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

        Assert.Null(engine.Scenario!.AsdexSafetyLogicConfig);
    }

    [Fact]
    public void Config_RoundTripsThroughASnapshot()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        AsdexSafetyLogicConfig config = Config(isClosed: true);
        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", config));

        StateSnapshotDto snapshot = engine.CaptureSnapshot();

        // Through the serializer a recording writes the snapshot with, not just the in-memory DTO: the nested runway
        // ring is the part a plain object graph would hide.
        StateSnapshotDto reread = JsonSerializer.Deserialize<StateSnapshotDto>(JsonSerializer.Serialize(snapshot))!;

        engine.Scenario!.AsdexSafetyLogicConfig = null;
        engine.RestoreFromSnapshot(reread);

        AssertMatches(config, engine.Scenario!.AsdexSafetyLogicConfig);
    }

    [Fact]
    public void Config_AbsentFromASnapshot_RestoresAsNull()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        StateSnapshotDto snapshot = engine.CaptureSnapshot();
        Assert.Null(snapshot.Scenario.AsdexSafetyLogicConfig);

        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Config(isClosed: true)));
        engine.RestoreFromSnapshot(snapshot);

        Assert.Null(engine.Scenario!.AsdexSafetyLogicConfig);
    }

    [Fact]
    public void Record_RoundTripsThroughRecordingJson()
    {
        AsdexSafetyLogicConfig config = Config(isClosed: true);
        var change = new RecordedAsdexSafetyLogicChange(42.5, "OAK", config);

        string json = JsonSerializer.Serialize<RecordedAction>(change, RecordingJsonOptions.Default);
        var reread = (RecordedAsdexSafetyLogicChange)JsonSerializer.Deserialize<RecordedAction>(json, RecordingJsonOptions.Default)!;

        Assert.Equal(42.5, reread.ElapsedSeconds);
        Assert.Equal("OAK", reread.FacilityId);
        AssertMatches(config, reread.Config);
    }

    [Fact]
    public void ScenarioUnload_ClearsTheConfig()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.ApplyRecordedAsdexSafetyLogic(new RecordedAsdexSafetyLogicChange(0, "OAK", Config(isClosed: true)));
        Assert.NotNull(engine.Scenario!.AsdexSafetyLogicConfig);

        // The engine-level unload/reload: a fresh scenario replaces the state the configuration lives on, so the
        // configuration — which tracks a runway configuration the reload is expected to change — goes with it.
        engine.LoadScenario(AiTestFixture.ParkedAtOak, 7, new DateTime(2026, 8, 26, 12, 0, 0, DateTimeKind.Utc));

        Assert.Null(engine.Scenario!.AsdexSafetyLogicConfig);
    }
}
