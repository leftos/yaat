using System.Text.Json;
using Microsoft.Extensions.Logging;
using Xunit;
using Yaat.Sim;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Soak;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// The active-runway list survives a snapshot: it rides in the scenario DTO as airport → token list, round-trips
/// through JSON with no navigation-database check, writes no field while the room has named none, and a snapshot that
/// predates the field restores with none. A null list, a null entry or a bad token drops that airport and is logged
/// rather than throwing.
/// </summary>
public sealed class ActiveRunwaysSnapshotTests : IDisposable
{
    private const string OlderSnapshotWithoutRunways =
        "{ \"SchemaVersion\": 32, \"ElapsedSeconds\": 0, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 }, \"Aircraft\": [],"
        + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"SimRate\": 1 } }";

    private readonly CapturingSimLogProvider _tap = new(LogLevel.Warning, 100);

    public ActiveRunwaysSnapshotTests(ITestOutputHelper output) => SimLogBuilder.CreateForTest(output).CaptureInto(_tap).InitializeSimLog();

    public void Dispose() => _tap.Dispose();

    [Fact]
    public void RoundTrip_PreservesListsAndUse()
    {
        SimScenarioState state = NewScenarioState();
        state.ActiveRunways = ActiveRunways
            .Empty.With(
                "OAK",
                [
                    new ActiveRunway("28L", ActiveRunwayUse.Departure),
                    new ActiveRunway("28R", ActiveRunwayUse.Arrival),
                    new ActiveRunway("30", ActiveRunwayUse.Both),
                ]
            )
            .With("SFO", [new ActiveRunway("10L", ActiveRunwayUse.Both)]);

        // state → DTO → JSON → DTO → state.
        ScenarioSnapshotDto dto = state.ToSnapshot();
        string json = JsonSerializer.Serialize(dto, RecordingJsonOptions.Default);
        ScenarioSnapshotDto restoredDto = JsonSerializer.Deserialize<ScenarioSnapshotDto>(json, RecordingJsonOptions.Default)!;
        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(Wrap(restoredDto));

        ActiveRunways restored = engine.Scenario!.ActiveRunways;
        Assert.Equal(state.ActiveRunways, restored);
        ActiveRunway[] expectedOak = [new("28L", ActiveRunwayUse.Departure), new("28R", ActiveRunwayUse.Arrival), new("30", ActiveRunwayUse.Both)];
        Assert.Equal(expectedOak, restored.For("OAK"));
        string[] expectedAirports = ["OAK", "SFO"];
        Assert.Equal(expectedAirports, restored.Airports);
    }

    [Fact]
    public void EmptyState_WritesNoField()
    {
        ScenarioSnapshotDto dto = NewScenarioState().ToSnapshot();

        // The field is left out of the JSON while the room has named none, so a snapshot with no runways is
        // byte-identical to one written before this field existed.
        Assert.Null(dto.ActiveRunways);
        Assert.DoesNotContain("\"ActiveRunways\"", JsonSerializer.Serialize(dto, RecordingJsonOptions.Default), StringComparison.Ordinal);

        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(Wrap(dto));

        Assert.Equal(ActiveRunways.Empty, engine.Scenario!.ActiveRunways);
    }

    [Fact]
    public void OlderSnapshot_WithoutField_RestoresNoRunways()
    {
        StateSnapshotDto snapshot = JsonSerializer.Deserialize<StateSnapshotDto>(OlderSnapshotWithoutRunways, RecordingJsonOptions.Default)!;

        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(snapshot);

        Assert.Equal(ActiveRunways.Empty, engine.Scenario!.ActiveRunways);
    }

    [Fact]
    public void Restore_NullRunwayList_DropsThatAirportWithAWarning()
    {
        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(WithRunways("""{ "OAK": null, "SFO": ["10L"] }"""));

        AssertNullRunwayListBehaviour(engine, "Snapshot active runways: OAK has no runway list");
    }

    [Fact]
    public void Restore_NullRunwayEntry_DropsThatAirportWithAWarning()
    {
        SimulationEngine engine = NewEngine();
        engine.RestoreFromSnapshot(WithRunways("""{ "OAK": ["28L", null], "SFO": ["10L"] }"""));

        AssertNullRunwayListBehaviour(engine, "Snapshot active runways: OAK has a null runway entry");
    }

    [Fact]
    public void Restore_BadToken_DropsThatAirportAndLogs()
    {
        SimulationEngine engine = NewEngine();

        // One element is one token: "28L 28R" is a single bad entry, never two good ones.
        engine.RestoreFromSnapshot(WithRunways("""{ "OAK": ["28L 28R"], "SFO": ["10L"] }"""));

        ActiveRunways restored = engine.Scenario!.ActiveRunways;
        Assert.Empty(restored.For("OAK"));
        Assert.Equal("10L", Assert.Single(restored.For("SFO")).Designator);
        Assert.Contains(
            _tap.Drain(),
            r =>
                (r.Category == "SimulationEngine")
                && r.Message.Contains("Snapshot active runways: OAK: Not a runway: 28L 28R", StringComparison.Ordinal)
        );
    }

    private void AssertNullRunwayListBehaviour(SimulationEngine engine, string expectedWarning)
    {
        ActiveRunways restored = engine.Scenario!.ActiveRunways;
        Assert.Empty(restored.For("OAK"));
        Assert.Equal("10L", Assert.Single(restored.For("SFO")).Designator);
        Assert.Contains(_tap.Drain(), r => (r.Category == "SimulationEngine") && r.Message.Contains(expectedWarning, StringComparison.Ordinal));
    }

    private static StateSnapshotDto WithRunways(string runwaysJson)
    {
        string json =
            "{ \"SchemaVersion\": 33, \"ElapsedSeconds\": 0, \"Rng\": { \"S0\": 1, \"S1\": 2, \"S2\": 3, \"S3\": 4 }, \"Aircraft\": [],"
            + " \"Scenario\": { \"ScenarioId\": \"t\", \"ScenarioName\": \"T\", \"RngSeed\": 1, \"SimRate\": 1, \"ActiveRunways\": "
            + runwaysJson
            + " } }";
        return JsonSerializer.Deserialize<StateSnapshotDto>(json, RecordingJsonOptions.Default)!;
    }

    private static SimScenarioState NewScenarioState() =>
        new()
        {
            ScenarioId = "test",
            ScenarioName = "Test",
            RngSeed = 42,
            OriginalScenarioJson = "{}",
        };

    private static SimulationEngine NewEngine() => new(new TestAirportGroundData()) { Scenario = NewScenarioState() };

    private static StateSnapshotDto Wrap(ScenarioSnapshotDto scenario) =>
        new()
        {
            ElapsedSeconds = 0,
            Rng = new RngState(1, 2, 3, 4),
            Aircraft = [],
            Scenario = scenario,
        };
}
