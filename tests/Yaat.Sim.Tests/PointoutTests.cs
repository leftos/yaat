using Xunit;
using Yaat.Sim.Commands;

namespace Yaat.Sim.Tests;

public class PointoutTests
{
    private static AircraftState MakeAircraft(TrackOwner? owner = null) =>
        new()
        {
            Callsign = "N98W",
            AircraftType = "C172",
            Track = new AircraftTrack { Owner = owner },
        };

    private static TrackOwner MakeOwner(string callsign, int subset, string sectorId) => new(callsign, "NCT", subset, sectorId, TrackOwnerType.Stars);

    private static Tcp MakeTcp(int subset, string sectorId) => new(subset, sectorId, $"tcp-{subset}{sectorId}", null);

    private static StarsPointout MakePendingPointout(int recipientSubset, string recipientSector, int senderSubset, string senderSector) =>
        new(MakeTcp(recipientSubset, recipientSector), MakeTcp(senderSubset, senderSector));

    private static Yaat.Sim.Simulation.SimScenarioState MinimalScenario() =>
        new()
        {
            ScenarioId = "s",
            ScenarioName = "s",
            RngSeed = 0,
            OriginalScenarioJson = "{}",
        };

    // ── PO no-args: accept inbound pointout ──

    [Fact]
    public void PoNoArgs_AcceptsInboundPointout()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        TrackOwner recipient = MakeOwner("NCT_APP", 2, "N");

        CommandResult result = TrackEngine.HandlePointOutNoArgs(ac, recipient);

        Assert.True(result.Success);
        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout.Status);
    }

    // ── PO no-args: retract outbound pointout ──

    [Fact]
    public void PoNoArgs_RetractsOutboundPointout()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        TrackOwner sender = MakeOwner("NCT_CTR", 1, "D");

        CommandResult result = TrackEngine.HandlePointOutNoArgs(ac, sender);

        Assert.True(result.Success);
        Assert.Null(ac.Track.Pointout);
    }

    // ── PO no-args: no pending pointout ──

    [Fact]
    public void PoNoArgs_NoPendingPointout_ReturnsError()
    {
        AircraftState ac = MakeAircraft();
        TrackOwner identity = MakeOwner("NCT_APP", 2, "N");

        CommandResult result = TrackEngine.HandlePointOutNoArgs(ac, identity);

        Assert.False(result.Success);
    }

    // ── PO no-args: unrelated identity ──

    [Fact]
    public void PoNoArgs_UnrelatedIdentity_ReturnsError()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        TrackOwner unrelated = MakeOwner("NCT_DEP", 3, "B");

        CommandResult result = TrackEngine.HandlePointOutNoArgs(ac, unrelated);

        Assert.False(result.Success);
    }

    // ── HandlePointOut: reject when pending exists ──

    [Fact]
    public void HandlePointOut_RejectsWhenPendingExists()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        StarsPointout originalPo = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout = originalPo;

        CommandResult result = TrackEngine.HandlePointOut(ac, MakeTcp(3, "B"), MakeTcp(1, "D"), elapsedSeconds: 0);

        Assert.False(result.Success);
        Assert.Same(originalPo, ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── HandlePointOut: allows overwrite when accepted ──

    [Fact]
    public void HandlePointOut_AllowsWhenAccepted()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        Tcp newTarget = MakeTcp(3, "B");
        Tcp senderTcp = MakeTcp(1, "D");
        CommandResult result = TrackEngine.HandlePointOut(ac, newTarget, senderTcp, elapsedSeconds: 0);

        Assert.True(result.Success);
        Assert.Equal("3B", ac.Track.Pointout!.Recipient.ToString());
    }

    // ── HandlePointOut: allows overwrite when rejected ──

    [Fact]
    public void HandlePointOut_AllowsWhenRejected()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Rejected;

        Tcp newTarget = MakeTcp(3, "B");
        Tcp senderTcp = MakeTcp(1, "D");
        CommandResult result = TrackEngine.HandlePointOut(ac, newTarget, senderTcp, elapsedSeconds: 0);

        Assert.True(result.Success);
        Assert.Equal("3B", ac.Track.Pointout!.Recipient.ToString());
    }

    // ── Regression: handoff does not clear pointout ──

    [Fact]
    public void Handoff_DoesNotClearPointout()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        TrackOwner target = MakeOwner("NCT_APP", 2, "N");
        ac.Track.HandoffPeer = target;
        ac.Track.HandoffInitiatedAt = 100;

        // Accept the handoff
        TrackEngine.HandleAccept(ac, MinimalScenario());

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── Regression: drop does not clear pointout ──

    [Fact]
    public void Drop_DoesNotClearPointout()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        TrackEngine.HandleDrop(ac);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── Regression: force handoff does not clear pointout ──

    [Fact]
    public void ForceHandoff_DoesNotClearPointout()
    {
        TrackOwner owner = MakeOwner("NCT_CTR", 1, "D");
        AircraftState ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        TrackOwner target = MakeOwner("NCT_APP", 2, "N");

        // Force handoff transfers ownership directly
        ac.Track.Owner = target;
        ac.Track.HandoffPeer = null;
        ac.Track.HandoffInitiatedAt = null;
        ac.Track.HandoffRedirectedBy = null;

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── Pointout responses infer the acting position from the pointout (no AS / identity) ──

    [Fact]
    public void HandleAcknowledge_AcceptsPendingPointout()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        CommandResult result = TrackEngine.HandleAcknowledge(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void HandleRejectPointout_RejectsPendingPointout()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        CommandResult result = TrackEngine.HandleRejectPointout(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Rejected, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void HandleRetractPointout_ClearsPendingPointout()
    {
        AircraftState ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        CommandResult result = TrackEngine.HandleRetractPointout(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Track.Pointout);
    }

    // ── Accept sets the recipient's recently-accepted shared-state flag ──
    // CRC keeps the recipient's datablock yellow (forced full) after they slew to accept until they
    // slew a second time to clear. That window is carried by the per-TCP
    // IsRecentlyAcceptedIncomingPointout shared-state flag, which CRC reads back from the track DTO
    // (it never originates the flag locally). Accepting the pointout must set it.

    [Fact]
    public void HandleAcknowledge_SetsRecipientRecentlyAcceptedFlag()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D"); // recipient TCP id = "tcp-2N"

        CommandResult result = TrackEngine.HandleAcknowledge(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);
        Assert.True(ac.Stars.SharedState.TryGetValue("tcp-2N", out StarsTrackSharedState? shared));
        Assert.True(shared!.IsRecentlyAcceptedIncomingPointout);
    }

    [Fact]
    public void PoNoArgs_Accept_SetsRecipientRecentlyAcceptedFlag()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D"); // recipient TCP id = "tcp-2N"
        TrackOwner recipient = MakeOwner("NCT_APP", 2, "N");

        CommandResult result = TrackEngine.HandlePointOutNoArgs(ac, recipient);

        Assert.True(result.Success, result.Message);
        Assert.True(ac.Stars.SharedState.TryGetValue("tcp-2N", out StarsTrackSharedState? shared));
        Assert.True(shared!.IsRecentlyAcceptedIncomingPointout);
    }

    [Fact]
    public void HandleAcknowledge_PreservesExistingRecipientSharedState()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Stars.SharedState["tcp-2N"] = new StarsTrackSharedState { ForceFdb = true, LeaderDirection = 3 };

        TrackEngine.HandleAcknowledge(ac);

        StarsTrackSharedState shared = ac.Stars.SharedState["tcp-2N"];
        Assert.True(shared.IsRecentlyAcceptedIncomingPointout);
        Assert.True(shared.ForceFdb);
        Assert.Equal(3, shared.LeaderDirection);
    }

    [Fact]
    public void HandleRejectPointout_DoesNotSetRecentlyAcceptedFlag()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        TrackEngine.HandleRejectPointout(ac);

        Assert.False(ac.Stars.SharedState.TryGetValue("tcp-2N", out StarsTrackSharedState? shared) && shared!.IsRecentlyAcceptedIncomingPointout);
    }

    // ── ClearDismissedIncomingPointout: recipient slew-to-clear drops a completed pointout ──

    [Fact]
    public void ClearDismissedIncomingPointout_ClearsAcceptedPointout_OnFlagFlipFalse()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        TrackEngine.ClearDismissedIncomingPointout(ac, MakeTcp(2, "N").Id, wasRecentlyAccepted: true, isRecentlyAccepted: false);

        Assert.Null(ac.Track.Pointout);
    }

    [Fact]
    public void ClearDismissedIncomingPointout_KeepsPendingPointout()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D"); // still Pending

        TrackEngine.ClearDismissedIncomingPointout(ac, MakeTcp(2, "N").Id, wasRecentlyAccepted: true, isRecentlyAccepted: false);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void ClearDismissedIncomingPointout_KeepsWhenFlagStillSet()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        // Not a true->false transition — the student has not slewed to clear yet.
        TrackEngine.ClearDismissedIncomingPointout(ac, MakeTcp(2, "N").Id, wasRecentlyAccepted: true, isRecentlyAccepted: true);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void ClearDismissedIncomingPointout_KeepsWhenNoPriorAcceptedFlag()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        // Flag was already false (e.g. an unrelated shared-state update arriving before CRC pushes
        // the recently-accepted flag) — must not clear prematurely.
        TrackEngine.ClearDismissedIncomingPointout(ac, MakeTcp(2, "N").Id, wasRecentlyAccepted: false, isRecentlyAccepted: false);

        Assert.NotNull(ac.Track.Pointout);
    }

    [Fact]
    public void ClearDismissedIncomingPointout_IgnoresWrongRecipient()
    {
        AircraftState ac = MakeAircraft(MakeOwner("NCT_CTR", 1, "D"));
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        // A different TCP's shared-state update must not clear this recipient's pointout.
        TrackEngine.ClearDismissedIncomingPointout(ac, MakeTcp(3, "B").Id, wasRecentlyAccepted: true, isRecentlyAccepted: false);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);
    }
}
