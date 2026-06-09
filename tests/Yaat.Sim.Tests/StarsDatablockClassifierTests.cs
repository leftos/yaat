using Xunit;

namespace Yaat.Sim.Tests;

/// <summary>
/// Unit tests for <see cref="StarsDatablockClassifier"/>: the student-scope STARS datablock view
/// (color / FDB-PDB-LDB level / leader direction). Logic mirrors CRC's
/// <c>DisplayElementTracks.BuildFdb/BuildPdb/BuildLdb</c> and the <c>DataBlockFormat</c> determination.
/// Pure logic — no NavData required.
/// </summary>
public class StarsDatablockClassifierTests
{
    private const string Facility = "OAK";
    private static readonly Tcp StudentTcp = new(2, "S", "student-tcp-id", null);
    private static readonly TrackOwner StudentPosition = TrackOwner.CreateStars("OAK_S_APP", Facility, 2, "S");
    private static readonly Tcp OtherTcp = new(3, "O", "other-tcp-id", null);
    private static readonly TrackOwner OtherPosition = TrackOwner.CreateStars("OAK_O_APP", Facility, 3, "O");

    private static AircraftState Aircraft() =>
        new()
        {
            Callsign = "N172SP",
            AircraftType = "C172",
            Position = new LatLon(37.7, -122.2),
            TrueHeading = new TrueHeading(90),
            Altitude = 3000,
        };

    private static StarsScopeView Classify(AircraftState ac) => StarsDatablockClassifier.Classify(ac, StudentTcp, StudentPosition);

    [Fact]
    public void OwnedByStudent_IsWhiteFullBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void OwnedByAnother_IsGreenPartialBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Partial, view.Level);
    }

    [Fact]
    public void Unassociated_IsGreenLimitedBlock()
    {
        var ac = Aircraft(); // Track.Owner stays null

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Limited, view.Level);
    }

    [Fact]
    public void PointoutToStudent_IsYellowFullBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Pending };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void PointoutToOther_DoesNotAffectStudentView()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(OtherTcp, StudentTcp) { Status = StarsPointoutStatus.Pending };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Partial, view.Level);
    }

    [Fact]
    public void AcceptedPointoutToStudent_AfterFlagCleared_IsGreenNotYellow()
    {
        // After the student acknowledges a point-out and slews it to clear (CRC's transient
        // IsRecentlyAcceptedIncomingPointout flag is back to false), the accepted point-out must
        // not keep the recipient track yellow. CRC (DisplayElementTracks) colors the recipient
        // yellow only on a pending point-out or the transient flag — never on Accepted alone.
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Accepted };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Partial, view.Level);
    }

    [Fact]
    public void AcceptedPointoutToStudent_StaysYellowUntilCleared()
    {
        // The student slews a pending incoming point-out to accept it. CRC keeps the data block
        // yellow (forced full) until they slew a second time to clear. TrackEngine.HandleAcknowledge
        // sets the recipient's IsRecentlyAcceptedIncomingPointout flag, so the classifier (and CRC,
        // which reads the same flag from the track DTO) keeps the track yellow.
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Pending };

        var result = Yaat.Sim.Commands.TrackEngine.HandleAcknowledge(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void SharedState_IsKeyedByTcpId_NotSubsetSectorCode()
    {
        // Regression: writers (CRC handler, TickProcessor) key SharedState by Tcp.Id (the ULID).
        // The classifier must look up by the same key, not by ToString() ("{Subset}{SectorId}").
        Assert.NotEqual(StudentTcp.Id, StudentTcp.ToString());

        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void HighlightedByStudent_IsCyan()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void HighlightOverridesOwnedColor()
    {
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void RecentlyAcceptedPointout_IsYellowFullBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void RecentlyAcceptedPointout_DoesNotOverrideOwnedWhite()
    {
        // A track the student owns stays white even if the recently-accepted flag lingers — CRC only
        // colors an owned track yellow for a forced pointout, which YAAT does not model.
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void HighlightOverridesRecentlyAcceptedPointout()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true, IsHighlighted = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void IncomingHandoffToStudent_IsWhiteFullBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.HandoffPeer = StudentPosition;

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void WasPreviouslyOwnedByStudent_IsWhiteFullBlock()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { WasPreviouslyOwned = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void ForceFdbByStudent_PromotesPartialToFull_StaysGreen()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { ForceFdb = true };

        var view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void GlobalLeaderDirection_TakesPrecedence()
    {
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.GlobalLeaderDirection = 8; // N
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { LeaderDirection = 2 }; // S

        var view = Classify(ac);

        Assert.Equal(8, view.LeaderDirection);
    }

    [Fact]
    public void PerTcpLeaderDirection_UsedWhenNoGlobal()
    {
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { LeaderDirection = 6 }; // E

        var view = Classify(ac);

        Assert.Equal(6, view.LeaderDirection);
    }

    [Fact]
    public void LeaderDirection_DefaultsWhenUnset()
    {
        var ac = Aircraft();
        ac.Track.Owner = StudentPosition;

        var view = Classify(ac);

        Assert.Equal(StarsDatablockClassifier.DefaultLeaderDirection, view.LeaderDirection);
    }

    [Fact]
    public void NoStudentPosition_ReturnsNeutralDefault()
    {
        var ac = Aircraft();
        ac.Track.Owner = OtherPosition;

        var view = StarsDatablockClassifier.Classify(ac, null, null);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
        Assert.Equal(StarsDatablockClassifier.DefaultLeaderDirection, view.LeaderDirection);
    }
}
