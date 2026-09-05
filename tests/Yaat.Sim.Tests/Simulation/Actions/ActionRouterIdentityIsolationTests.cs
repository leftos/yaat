using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.ControllerAi;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Snapshots;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The engine holds one per-connection active-position map, and two callers share it:
/// <see cref="SimulationEngine.ReplayCommand"/> (replay) and <see cref="SimulationEngine.DispatchAiCommand"/>
/// (live). An AI controller's commands are recorded under its own connection id, so a recorded
/// <c>AS</c> carrying that id lands in the same map slot the live AI resolves its identity from.
/// An AI position's identity is the position it works; a selection made on another connection —
/// or replayed from a recording — must not displace it.
/// </summary>
public class ActionRouterIdentityIsolationTests
{
    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public ActionRouterIdentityIsolationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    [Fact]
    public void ReplayedActivePosition_DoesNotDisplaceTheAiPositionsOwnIdentity()
    {
        if (_zoa is null)
        {
            return;
        }

        var ground = TestAiPositions.OakGround(_zoa);
        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, [ground]);
        var scenario = engine.Scenario!;

        // A student on OAK Tower, so "AS 3T" resolves to an owner that is not the AI ground position.
        var student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
        scenario.StudentPosition = student;
        scenario.StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null);

        var aiConnectionId = AiConnectionId.Format(ground.PositionId);
        var aiOwner = _zoa.ResolvePosition(ground.PositionId);
        Assert.NotNull(aiOwner);
        Assert.NotEqual(student, aiOwner);

        // A recorded active-position selection carrying the AI's connection id, replayed into this engine.
        engine.Actions.Apply(new RecordedCommand(0, AiTestFixture.Callsign, "AS 3T", "XX", aiConnectionId));

        var result = engine.DispatchAiCommand(ground, AiTestFixture.Callsign, "TRACK");

        Assert.True(result.Success, result.Message);
        var aircraft = engine.FindAircraft(AiTestFixture.Callsign)!;
        Assert.Equal(aiOwner, aircraft.Track.Owner);

        // The recorded selection landed in the engine's one map, under the AI connection id, and stayed there.
        Assert.True(engine.PositionSelections.TryGet(aiConnectionId, out var selected));
        Assert.Equal(student, selected);
    }

    [Fact]
    public void PositionSelections_SurviveASnapshotRoundTrip()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        var dep = TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U");
        engine.PositionSelections.Select("conn-1", dep);

        StateSnapshotDto snapshot = engine.CaptureSnapshot(0);
        Assert.NotNull(snapshot.Server?.PositionSelections);
        Assert.Equal("SFO_DEP", snapshot.Server.PositionSelections["conn-1"].Callsign);

        engine.PositionSelections.Clear();
        engine.PositionSelections.Select("conn-2", dep);
        Assert.Equal(scenario.StudentPosition, engine.ResolveIdentity("conn-1", null));

        engine.RestoreFromSnapshot(snapshot);

        Assert.Equal(dep, engine.ResolveIdentity("conn-1", null));
        Assert.False(engine.PositionSelections.TryGet("conn-2", out _));
    }

    [Fact]
    public void PreFeatureSnapshot_RestoresAnEmptyMap()
    {
        if (_zoa is null)
        {
            return;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var snapshot = engine.CaptureSnapshot(0);
        var withoutSelections = new StateSnapshotDto
        {
            SchemaVersion = snapshot.SchemaVersion,
            ElapsedSeconds = snapshot.ElapsedSeconds,
            Rng = snapshot.Rng,
            WeatherJson = snapshot.WeatherJson,
            Aircraft = snapshot.Aircraft,
            Scenario = snapshot.Scenario,
            Server = new ServerSnapshotDto
            {
                ConsolidationOverrides = snapshot.Server!.ConsolidationOverrides,
                ActiveConflicts = snapshot.Server.ActiveConflicts,
                EramConflicts = snapshot.Server.EramConflicts,
                BeaconCodePool = snapshot.Server.BeaconCodePool,
            },
        };
        engine.PositionSelections.Select("conn-1", TrackOwner.CreateStars("SFO_DEP", "NCT", 4, "U"));

        engine.RestoreFromSnapshot(withoutSelections);

        Assert.Empty(engine.PositionSelections.Snapshot());
    }
}
