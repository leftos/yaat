using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Bug "HO and ACCEPT should not require AS [TCP]" — Sim-side coverage of the inference path
/// used during replay (<see cref="TrackEngine.Dispatch"/> via the ActionRouter's track arm). The acting
/// position is inferred from the track itself: the current owner for HO/DROP/CANCEL, the handoff
/// peer for ACCEPT. The issuer's resolved identity no longer has to match, and a null identity no
/// longer blocks these commands.
/// </summary>
public class TrackEngineInferenceTests
{
    private static TrackOwner Owner(string callsign, int subset, string sectorId) => TrackOwner.CreateStars(callsign, "ZOA", subset, sectorId);

    private static AircraftState Aircraft() => new() { Callsign = "N123AB", AircraftType = "C172" };

    private static SimScenarioState Scenario(TrackOwner studentPosition, double elapsedSeconds = 0) =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
            ElapsedSeconds = elapsedSeconds,
            StudentPosition = studentPosition,
        };

    [Fact]
    public void HandleAccept_AcceptsHandoffPeer_WithoutIdentity()
    {
        AircraftState ac = Aircraft();
        TrackOwner peer = Owner("OAK_G_APP", 3, "G");
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");
        ac.Track.HandoffPeer = peer;

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(Owner("OAK_TWR", 3, "O")));

        Assert.True(result.Success, result.Message);
        Assert.Equal(peer, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.True(ac.Track.HandoffAccepted);
    }

    [Fact]
    public void HandleAccept_NoPendingHandoff_ReturnsError()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");

        CommandResult result = TrackEngine.HandleAccept(ac, Scenario(Owner("OAK_TWR", 3, "O")));

        Assert.False(result.Success);
        Assert.Contains("No pending handoff", result.Message ?? "");
    }

    [Fact]
    public void ApplyHandoff_InfersOwner_HandsToStudent()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Owner("OAK_DEP", 4, "R");
        SimScenarioState scenario = Scenario(Owner("OAK_TWR", 3, "O"), elapsedSeconds: 42);

        CommandResult result = TrackEngine.ApplyHandoff(ac, scenario, identity: null, tcpCode: null, redirect: null);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("OAK_TWR", ac.Track.HandoffPeer.Callsign);
        Assert.Equal(42, ac.Track.HandoffInitiatedAt);
    }

    [Fact]
    public void ApplyHandoff_Untracked_ReturnsNotTracked()
    {
        AircraftState ac = Aircraft();
        SimScenarioState scenario = Scenario(Owner("OAK_TWR", 3, "O"));

        CommandResult result = TrackEngine.ApplyHandoff(ac, scenario, identity: null, tcpCode: null, redirect: null);

        Assert.False(result.Success);
        Assert.Contains("not tracked", result.Message ?? "");
    }

    [Fact]
    public void Dispatch_InitiateHandoff_SucceedsWithNullIdentity()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Owner("OAK_DEP", 4, "R");
        SimScenarioState scenario = Scenario(Owner("OAK_TWR", 3, "O"), elapsedSeconds: 10);

        CommandResult? result = TrackEngine.Dispatch(
            new InitiateHandoffCommand(null),
            ac,
            new TrackDispatchContext(Identity: null, scenario, Redirect: null, new ConflictAlertState())
        );

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("OAK_TWR", ac.Track.HandoffPeer.Callsign);
    }

    [Fact]
    public void Dispatch_Accept_SucceedsWithNullIdentity()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");
        ac.Track.HandoffPeer = Owner("SFO_DEP", 4, "U");
        SimScenarioState scenario = Scenario(Owner("OAK_TWR", 3, "O"));

        CommandResult? result = TrackEngine.Dispatch(
            new AcceptHandoffCommand(),
            ac,
            new TrackDispatchContext(Identity: null, scenario, Redirect: null, new ConflictAlertState())
        );

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.Equal("SFO_DEP", ac.Track.Owner.Callsign);
    }

    [Fact]
    public void HandleTrack_FrozenUnownedTrack_UnfreezesAndTracks()
    {
        AircraftState ac = Aircraft();
        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = 37.6;
        ac.Eram.FrozenLon = -122.0;
        ac.Eram.FrozenAltitude = 350;
        var identity = TrackOwner.CreateEram("ZOA_36", "ZOA", "36");

        CommandResult result = TrackEngine.HandleTrack(ac, identity);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Eram.IsFrozen);
        Assert.Null(ac.Eram.FrozenLat);
        Assert.Null(ac.Eram.FrozenLon);
        Assert.Null(ac.Eram.FrozenAltitude);
        Assert.Equal("ZOA_36", ac.Track.Owner!.Callsign);
    }

    [Fact]
    public void HandleTrack_FrozenTrackOwnedBySelf_Unfreezes()
    {
        AircraftState ac = Aircraft();
        var identity = TrackOwner.CreateEram("ZOA_36", "ZOA", "36");
        ac.Track.Owner = identity;
        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = 37.6;
        ac.Eram.FrozenLon = -122.0;
        ac.Eram.FrozenAltitude = 350;

        CommandResult result = TrackEngine.HandleTrack(ac, identity);

        Assert.True(result.Success, result.Message);
        Assert.False(ac.Eram.IsFrozen);
        Assert.Null(ac.Eram.FrozenLat);
    }

    [Fact]
    public void HandleTrack_FrozenTrackOwnedByOther_RejectedAndStaysFrozen()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = TrackOwner.CreateEram("ZOA_36", "ZOA", "36");
        ac.Eram.IsFrozen = true;
        ac.Eram.FrozenLat = 37.6;
        var other = TrackOwner.CreateEram("ZOA_40", "ZOA", "40");

        CommandResult result = TrackEngine.HandleTrack(ac, other);

        Assert.False(result.Success);
        Assert.True(ac.Eram.IsFrozen);
        Assert.Equal("ZOA_36", ac.Track.Owner!.Callsign);
    }
}
