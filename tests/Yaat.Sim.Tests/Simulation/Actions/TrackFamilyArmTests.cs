using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;
using Yaat.Sim.Simulation;
using Yaat.Sim.Simulation.Actions;
using Yaat.Sim.Testing;
using Yaat.Sim.Tests.ControllerAi;
using Yaat.Sim.Tests.Helpers;

namespace Yaat.Sim.Tests.Simulation.Actions;

/// <summary>
/// The track-family arms whose bodies live in <see cref="TrackEngine"/>: <c>ACCEPTALL</c> / <c>HOALL</c> under
/// the resolved identity, <c>GHOST</c> (a staggered phantom, or an overlay that never steals another position's track),
/// <c>RPOSLOC</c> / <c>RPOSMOVE</c> on the aircraft's datablock, and <c>CAACK</c> on the engine's conflict alerts. Each is
/// applied from a record the way a replay does, so what the live server produced is what a replay produces.
/// </summary>
public class TrackFamilyArmTests
{
    private static readonly TrackOwner Student = TrackOwner.CreateStars("OAK_TWR", "OAK", 3, "T");
    private static readonly TrackOwner Nct4U = TrackOwner.CreateStars("NCT_4U", "NCT", 4, "U");

    private readonly ArtccConfigRoot? _zoa = TestArtccConfig.LoadZoa();

    public TrackFamilyArmTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private SimulationEngine? Engine()
    {
        if (_zoa is null)
        {
            return null;
        }

        var engine = AiTestFixture.Load(AiTestFixture.ParkedAtOak, _zoa, 7, []);
        var scenario = engine.Scenario!;
        scenario.StudentPosition = Student;
        scenario.StudentTcp = new Tcp(3, "T", "tcp-oak-twr", null);
        scenario.AtcPositions.Add(
            new ResolvedAtcPosition
            {
                Source = new ScenarioAtc(),
                Owner = Nct4U,
                Tcp = new Tcp(4, "U", "tcp-nct-4u", null),
            }
        );
        return engine;
    }

    private static RecordedCommand Recorded(string callsign, string command) => new(0, callsign, command, "XX", "conn-1");

    [Fact]
    public void AcceptAll_Replay_AcceptsTheHandoffsOfferedToTheResolvedIdentity()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4U;
        ac.Track.HandoffPeer = Student;

        var outcome = engine.Actions.Apply(Recorded("", "ACCEPTALL"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Accepted 1 handoff(s)", outcome.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.AcceptAllHandoffs, ActionScope.Position, IsHostSlot: false), outcome.Trace);
        Assert.True(ac.Track.Owner!.MatchesPosition(Student));
        Assert.Null(ac.Track.HandoffPeer);
        Assert.True(ac.Track.HandoffAccepted);
        Assert.Empty(engine.Scenario!.ActionLog);
    }

    [Fact]
    public void HandoffAll_Replay_OffersEveryOwnedTrackToTheTcp()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        var outcome = engine.Actions.Apply(Recorded("", "HOALL 4U"));

        Assert.True(outcome.Result.Success, outcome.Result.Message);
        Assert.Equal("Initiated handoff for 1 aircraft to 4U", outcome.Result.Message);
        Assert.True(ac.Track.HandoffPeer!.MatchesPosition(Nct4U));
        Assert.Equal(engine.Scenario!.ElapsedSeconds, ac.Track.HandoffInitiatedAt);
    }

    [Fact]
    public void GlobalTrack_WithoutAnIdentity_IsRefused()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        engine.Scenario!.StudentPosition = null;

        var outcome = engine.Actions.Apply(Recorded("", "ACCEPTALL"));

        Assert.False(outcome.Result.Success);
        Assert.Equal("No active position — use AS to set one", outcome.Result.Message);
    }

    [Fact]
    public void GhostTrack_Replay_CreatesAStaggeredPhantomOffTheRunway()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var first = engine.Actions.Apply(Recorded("", "GHOST GHOST1 28R"));
        var second = engine.Actions.Apply(Recorded("", "GHOST GHOST2 28R"));

        Assert.True(first.Result.Success, first.Result.Message);
        Assert.True(second.Result.Success, second.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.GhostTrack, ActionScope.Callsign, IsHostSlot: false), first.Trace);
        var runway = NavigationDatabase.Instance.GetRunway("OAK", "28R")!;
        var reciprocal = runway.TrueHeading.ToReciprocal();
        var ghost1 = engine.FindAircraft("GHOST1")!;
        var ghost2 = engine.FindAircraft("GHOST2")!;
        Assert.True(ghost1.Ghost.IsUnsupported);
        Assert.False(ghost1.Ghost.IsOverlay);
        Assert.Equal("28R", ghost1.Ghost.RunwayId);
        Assert.True(ghost1.Track.Owner!.MatchesPosition(Student));
        var (lat1, lon1) = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 0.1);
        var (lat2, lon2) = GeoMath.ProjectPoint(runway.ThresholdLatitude, runway.ThresholdLongitude, reciprocal, 0.2);
        Assert.Equal(new LatLon(lat1, lon1), ghost1.Position);
        Assert.Equal(new LatLon(lat2, lon2), ghost2.Position);
    }

    [Fact]
    public void GhostTrack_OverlaysAnExistingAircraft_UnlessAnotherPositionOwnsIt()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Nct4U;

        var refused = engine.Actions.Apply(Recorded("", $"GHOST {AiTestFixture.Callsign} 28R"));
        Assert.False(refused.Result.Success);
        Assert.Equal(
            $"{AiTestFixture.Callsign} owned by {TrackEngine.FormatOwner(Nct4U)}, not you — use AS to switch position, or HOF to force",
            refused.Result.Message
        );
        Assert.False(ac.Ghost.IsUnsupported);

        ac.Track.Owner = null;
        var overlaid = engine.Actions.Apply(Recorded("", $"GHOST {AiTestFixture.Callsign} 28R"));

        Assert.True(overlaid.Result.Success, overlaid.Result.Message);
        Assert.Equal($"Ghost overlay on {AiTestFixture.Callsign}", overlaid.Result.Message);
        Assert.True(ac.Ghost.IsUnsupported);
        Assert.True(ac.Ghost.IsOverlay);
        Assert.True(ac.Track.Owner!.MatchesPosition(Student));
        Assert.Single(engine.World.GetSnapshot());
    }

    [Fact]
    public void Reposition_Replay_ParksThenReassociatesTheDatablock()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var ac = engine.FindAircraft(AiTestFixture.Callsign)!;
        ac.Track.Owner = Student;

        var parked = engine.Actions.Apply(Recorded(AiTestFixture.Callsign, $"RPOSLOC {AiTestFixture.Callsign} 37.7 -122.2"));

        Assert.True(parked.Result.Success, parked.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.Reposition, ActionScope.Aircraft, IsHostSlot: false), parked.Trace);
        Assert.Equal(DataBlockBinding.Parked, ac.DataBlock.Binding);
        Assert.Equal($"RPOS{AiTestFixture.Callsign}", ac.DataBlock.DetachedId);
        Assert.Equal(37.7, ac.DataBlock.Latitude);
        Assert.Null(ac.Track.Owner);
        Assert.True(ac.DataBlock.CreatedBy!.MatchesPosition(Student));

        var rebound = engine.Actions.Apply(Recorded(AiTestFixture.Callsign, $"RPOSMOVE {AiTestFixture.Callsign} {AiTestFixture.Callsign}"));

        Assert.True(rebound.Result.Success, rebound.Result.Message);
        Assert.Equal(DataBlockBinding.Bound, ac.DataBlock.Binding);
        Assert.Null(ac.DataBlock.DetachedId);
        Assert.True(ac.Track.Owner!.MatchesPosition(Student));
    }

    [Fact]
    public void AcknowledgeConflictAlert_Replay_AcknowledgesTheAircraftsAlerts()
    {
        if (Engine() is not { } engine)
        {
            return;
        }

        var conflict = new ActiveConflict
        {
            Id = "c1",
            CallsignA = AiTestFixture.Callsign,
            CallsignB = "OTHER",
        };
        engine.ConflictAlerts.Conflicts[conflict.Id] = conflict;

        var acknowledged = engine.Actions.Apply(Recorded(AiTestFixture.Callsign, "CAACK"));
        var nothingLeft = engine.Actions.Apply(Recorded(AiTestFixture.Callsign, "CAACK"));

        Assert.True(acknowledged.Result.Success, acknowledged.Result.Message);
        Assert.Equal($"Acknowledged 1 conflict alert(s) for {AiTestFixture.Callsign}", acknowledged.Result.Message);
        Assert.Equal(new ActionTrace(RecordedCommandKind.TrackOwnership, ActionScope.Aircraft, IsHostSlot: false), acknowledged.Trace);
        Assert.True(conflict.IsAcknowledged);
        Assert.False(nothingLeft.Result.Success);
        Assert.Equal($"No active conflict alerts for {AiTestFixture.Callsign}", nothingLeft.Result.Message);
    }
}
