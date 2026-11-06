using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Snapshots;

/// <summary>
/// Snapshot schema 48: AircraftSnapshotDto.HasLeftStudentFrequency became <see cref="AircraftSnapshotDto.TunedPosition"/>. A
/// pre-V48 aircraft that had left the student's frequency restores tuned to <see cref="TrackOwner.Elsewhere"/> (the target
/// was never recorded); one that had not restores with no tuned position.
/// </summary>
public class SnapshotSchema48Tests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public SnapshotSchema48Tests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine Engine()
    {
        if (_zoa is null)
        {
            Assert.Skip("The ZOA ARTCC config is not available");
        }

        return AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
    }

    /// <summary>The engine's snapshot rewritten to schema 47, its aircraft carrying the legacy flag as <paramref name="left"/>.</summary>
    private static StateSnapshotDto AsSchema47(SimulationEngine engine, bool left)
    {
        JsonObject root = JsonNode.Parse(JsonSerializer.Serialize(engine.CaptureSnapshot()))!.AsObject();
        root["SchemaVersion"] = 47;
        foreach (JsonNode? aircraft in root["Aircraft"]!.AsArray())
        {
            JsonObject ac = aircraft!.AsObject();
            ac.Remove("TunedPosition");
            ac["HasLeftStudentFrequency"] = left;
        }

        return JsonSerializer.Deserialize<StateSnapshotDto>(root.ToJsonString())!;
    }

    [Fact]
    public void V47Snapshot_LeftTheStudentsFrequency_RestoresTunedElsewhere()
    {
        SimulationEngine engine = Engine();

        engine.RestoreFromSnapshot(AsSchema47(engine, left: true));

        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Equal(TrackOwner.Elsewhere, aircraft.TunedPosition);
        Assert.True(aircraft.IsTunedAwayFrom(null));
    }

    [Fact]
    public void V47Snapshot_Rewritten_DropsTheLegacyFlag()
    {
        SimulationEngine engine = Engine();
        StateSnapshotDto legacy = AsSchema47(engine, left: true);

        SnapshotSchemaMigrator.Migrate(legacy);

        JsonObject rewritten = JsonNode.Parse(JsonSerializer.Serialize(legacy))!.AsObject();
        Assert.Equal(SnapshotSchemaMigrator.CurrentSchemaVersion, (int)rewritten["SchemaVersion"]!);
        Assert.All(rewritten["Aircraft"]!.AsArray(), ac => Assert.False(ac!.AsObject().ContainsKey("HasLeftStudentFrequency")));
        Assert.All(rewritten["Aircraft"]!.AsArray(), ac => Assert.True(ac!.AsObject().ContainsKey("TunedPosition")));
    }

    [Fact]
    public void V47Snapshot_StillOnTheStudentsFrequency_RestoresUntuned()
    {
        SimulationEngine engine = Engine();

        engine.RestoreFromSnapshot(AsSchema47(engine, left: false));

        AircraftState aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Null(aircraft.TunedPosition);
        Assert.False(aircraft.IsTunedAwayFrom(null));
    }

    [Fact]
    public void TunedPosition_SurvivesASnapshotRoundTrip()
    {
        SimulationEngine engine = Engine();
        ArtccConfigRoot zoa = _zoa!;
        TrackOwner tower = zoa.ResolvePosition(zoa.FindPositionByCallsign("OAK_TWR", null)!.Id)!;
        engine.FindAircraft(AiTestFixture.Callsign)!.TunedPosition = tower;
        string json = JsonSerializer.Serialize(engine.CaptureSnapshot());

        engine.RestoreFromSnapshot(JsonSerializer.Deserialize<StateSnapshotDto>(json)!);

        Assert.Equal(tower, engine.FindAircraft(AiTestFixture.Callsign)!.TunedPosition);
    }
}
