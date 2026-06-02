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

    // ── PO no-args: accept inbound pointout ──

    [Fact]
    public void PoNoArgs_AcceptsInboundPointout()
    {
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        var recipient = MakeOwner("NCT_APP", 2, "N");

        var result = TrackEngine.HandlePointOutNoArgs(ac, recipient);

        Assert.True(result.Success);
        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout.Status);
    }

    // ── PO no-args: retract outbound pointout ──

    [Fact]
    public void PoNoArgs_RetractsOutboundPointout()
    {
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        var sender = MakeOwner("NCT_CTR", 1, "D");

        var result = TrackEngine.HandlePointOutNoArgs(ac, sender);

        Assert.True(result.Success);
        Assert.Null(ac.Track.Pointout);
    }

    // ── PO no-args: no pending pointout ──

    [Fact]
    public void PoNoArgs_NoPendingPointout_ReturnsError()
    {
        var ac = MakeAircraft();
        var identity = MakeOwner("NCT_APP", 2, "N");

        var result = TrackEngine.HandlePointOutNoArgs(ac, identity);

        Assert.False(result.Success);
    }

    // ── PO no-args: unrelated identity ──

    [Fact]
    public void PoNoArgs_UnrelatedIdentity_ReturnsError()
    {
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        var unrelated = MakeOwner("NCT_DEP", 3, "B");

        var result = TrackEngine.HandlePointOutNoArgs(ac, unrelated);

        Assert.False(result.Success);
    }

    // ── HandlePointOut: reject when pending exists ──

    [Fact]
    public void HandlePointOut_RejectsWhenPendingExists()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        var originalPo = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout = originalPo;

        var result = TrackEngine.HandlePointOut(ac, MakeTcp(3, "B"), MakeTcp(1, "D"));

        Assert.False(result.Success);
        Assert.Same(originalPo, ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── HandlePointOut: allows overwrite when accepted ──

    [Fact]
    public void HandlePointOut_AllowsWhenAccepted()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Accepted;

        var newTarget = MakeTcp(3, "B");
        var senderTcp = MakeTcp(1, "D");
        var result = TrackEngine.HandlePointOut(ac, newTarget, senderTcp);

        Assert.True(result.Success);
        Assert.Equal("3B", ac.Track.Pointout!.Recipient.ToString());
    }

    // ── HandlePointOut: allows overwrite when rejected ──

    [Fact]
    public void HandlePointOut_AllowsWhenRejected()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");
        ac.Track.Pointout.Status = StarsPointoutStatus.Rejected;

        var newTarget = MakeTcp(3, "B");
        var senderTcp = MakeTcp(1, "D");
        var result = TrackEngine.HandlePointOut(ac, newTarget, senderTcp);

        Assert.True(result.Success);
        Assert.Equal("3B", ac.Track.Pointout!.Recipient.ToString());
    }

    // ── Regression: handoff does not clear pointout ──

    [Fact]
    public void Handoff_DoesNotClearPointout()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        var target = MakeOwner("NCT_APP", 2, "N");
        ac.Track.HandoffPeer = target;
        ac.Track.HandoffInitiatedAt = 100;

        // Accept the handoff
        TrackEngine.HandleAccept(ac);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── Regression: drop does not clear pointout ──

    [Fact]
    public void Drop_DoesNotClearPointout()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        TrackEngine.HandleDrop(ac);

        Assert.NotNull(ac.Track.Pointout);
        Assert.Equal(StarsPointoutStatus.Pending, ac.Track.Pointout.Status);
    }

    // ── Regression: force handoff does not clear pointout ──

    [Fact]
    public void ForceHandoff_DoesNotClearPointout()
    {
        var owner = MakeOwner("NCT_CTR", 1, "D");
        var ac = MakeAircraft(owner);
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        var target = MakeOwner("NCT_APP", 2, "N");

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
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        var result = TrackEngine.HandleAcknowledge(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void HandleRejectPointout_RejectsPendingPointout()
    {
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        var result = TrackEngine.HandleRejectPointout(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Rejected, ac.Track.Pointout!.Status);
    }

    [Fact]
    public void HandleRetractPointout_ClearsPendingPointout()
    {
        var ac = MakeAircraft();
        ac.Track.Pointout = MakePendingPointout(2, "N", 1, "D");

        var result = TrackEngine.HandleRetractPointout(ac);

        Assert.True(result.Success, result.Message);
        Assert.Null(ac.Track.Pointout);
    }
}
