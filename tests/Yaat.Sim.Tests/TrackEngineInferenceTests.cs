using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Simulation;

namespace Yaat.Sim.Tests;

/// <summary>
/// Bug "HO and ACCEPT should not require AS [TCP]" — Sim-side coverage of the inference path
/// used during replay (<see cref="TrackEngine.Dispatch"/> via ReplayTrackApplier). The acting
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
        var ac = Aircraft();
        var peer = Owner("OAK_G_APP", 3, "G");
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");
        ac.Track.HandoffPeer = peer;

        var result = TrackEngine.HandleAccept(ac, Scenario(Owner("OAK_TWR", 3, "O")));

        Assert.True(result.Success, result.Message);
        Assert.Equal(peer, ac.Track.Owner);
        Assert.Null(ac.Track.HandoffPeer);
        Assert.True(ac.Track.HandoffAccepted);
    }

    [Fact]
    public void HandleAccept_NoPendingHandoff_ReturnsError()
    {
        var ac = Aircraft();
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");

        var result = TrackEngine.HandleAccept(ac, Scenario(Owner("OAK_TWR", 3, "O")));

        Assert.False(result.Success);
        Assert.Contains("No pending handoff", result.Message ?? "");
    }

    [Fact]
    public void ApplyHandoff_InfersOwner_HandsToStudent()
    {
        var ac = Aircraft();
        ac.Track.Owner = Owner("OAK_DEP", 4, "R");
        var scenario = Scenario(Owner("OAK_TWR", 3, "O"), elapsedSeconds: 42);

        var result = TrackEngine.ApplyHandoff(ac, scenario, tcpCode: null);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("OAK_TWR", ac.Track.HandoffPeer.Callsign);
        Assert.Equal(42, ac.Track.HandoffInitiatedAt);
    }

    [Fact]
    public void ApplyHandoff_Untracked_ReturnsNotTracked()
    {
        var ac = Aircraft();
        var scenario = Scenario(Owner("OAK_TWR", 3, "O"));

        var result = TrackEngine.ApplyHandoff(ac, scenario, tcpCode: null);

        Assert.False(result.Success);
        Assert.Contains("not tracked", result.Message ?? "");
    }

    [Fact]
    public void Dispatch_InitiateHandoff_SucceedsWithNullIdentity()
    {
        var ac = Aircraft();
        ac.Track.Owner = Owner("OAK_DEP", 4, "R");
        var scenario = Scenario(Owner("OAK_TWR", 3, "O"), elapsedSeconds: 10);

        var result = TrackEngine.Dispatch(new InitiateHandoffCommand(null), ac, identity: null, scenario);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.NotNull(ac.Track.HandoffPeer);
        Assert.Equal("OAK_TWR", ac.Track.HandoffPeer.Callsign);
    }

    [Fact]
    public void Dispatch_Accept_SucceedsWithNullIdentity()
    {
        var ac = Aircraft();
        ac.Track.Owner = Owner("OAK_TWR", 3, "O");
        ac.Track.HandoffPeer = Owner("SFO_DEP", 4, "U");
        var scenario = Scenario(Owner("OAK_TWR", 3, "O"));

        var result = TrackEngine.Dispatch(new AcceptHandoffCommand(), ac, identity: null, scenario);

        Assert.NotNull(result);
        Assert.True(result.Success, result.Message);
        Assert.Equal("SFO_DEP", ac.Track.Owner.Callsign);
    }
}
