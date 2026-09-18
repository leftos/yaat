using Xunit;
using Yaat.Sim.Commands;

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
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void OwnedByAnother_IsGreenPartialBlock()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Partial, view.Level);
    }

    [Fact]
    public void Unassociated_IsGreenLimitedBlock()
    {
        AircraftState ac = Aircraft(); // Track.Owner stays null

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Limited, view.Level);
    }

    [Fact]
    public void PointoutToStudent_IsYellowFullBlock()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Pending };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void PointoutToOther_DoesNotAffectStudentView()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(OtherTcp, StudentTcp) { Status = StarsPointoutStatus.Pending };

        StarsScopeView view = Classify(ac);

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
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Accepted };

        StarsScopeView view = Classify(ac);

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
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.Pointout = new StarsPointout(StudentTcp, OtherTcp) { Status = StarsPointoutStatus.Pending };

        CommandResult result = Yaat.Sim.Commands.TrackEngine.HandleAcknowledge(ac);

        Assert.True(result.Success, result.Message);
        Assert.Equal(StarsPointoutStatus.Accepted, ac.Track.Pointout!.Status);

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void SharedState_IsKeyedByTcpId_NotSubsetSectorCode()
    {
        // Regression: writers (CRC handler, TickProcessor) key SharedState by Tcp.Id (the ULID).
        // The classifier must look up by the same key, not by ToString() ("{Subset}{SectorId}").
        Assert.NotEqual(StudentTcp.Id, StudentTcp.ToString());

        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void HighlightedByStudent_IsCyan()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void HighlightOverridesOwnedColor()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsHighlighted = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void RecentlyAcceptedPointout_IsYellowFullBlock()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Pointout, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void RecentlyAcceptedPointout_DoesNotOverrideOwnedWhite()
    {
        // A track the student owns stays white even if the recently-accepted flag lingers — CRC only
        // colors an owned track yellow for a forced pointout, which YAAT does not model.
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void HighlightOverridesRecentlyAcceptedPointout()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { IsRecentlyAcceptedIncomingPointout = true, IsHighlighted = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Highlighted, view.Color);
    }

    [Fact]
    public void IncomingHandoffToStudent_IsWhiteFullBlock()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Track.HandoffPeer = StudentPosition;

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void WasPreviouslyOwnedByStudent_IsWhiteFullBlock()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { WasPreviouslyOwned = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Owned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void ForceFdbByStudent_PromotesPartialToFull_StaysGreen()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { ForceFdb = true };

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
    }

    [Fact]
    public void GlobalLeaderDirection_TakesPrecedence()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.GlobalLeaderDirection = 8; // N
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { LeaderDirection = 2 }; // S

        StarsScopeView view = Classify(ac);

        Assert.Equal(8, view.LeaderDirection);
    }

    [Fact]
    public void PerTcpLeaderDirection_UsedWhenNoGlobal()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;
        ac.Stars.SharedState[StudentTcp.Id] = new StarsTrackSharedState { LeaderDirection = 6 }; // E

        StarsScopeView view = Classify(ac);

        Assert.Equal(6, view.LeaderDirection);
    }

    [Fact]
    public void LeaderDirection_DefaultsWhenUnset()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = StudentPosition;

        StarsScopeView view = Classify(ac);

        Assert.Equal(StarsDatablockClassifier.DefaultLeaderDirection, view.LeaderDirection);
    }

    [Fact]
    public void NoStudentPosition_ReturnsNeutralDefault()
    {
        AircraftState ac = Aircraft();
        ac.Track.Owner = OtherPosition;

        StarsScopeView view = StarsDatablockClassifier.Classify(ac, null, null);

        Assert.Equal(StarsDatablockColor.Unowned, view.Color);
        Assert.Equal(StarsDatablockLevel.Full, view.Level);
        Assert.Equal(StarsDatablockClassifier.DefaultLeaderDirection, view.LeaderDirection);
    }
}
