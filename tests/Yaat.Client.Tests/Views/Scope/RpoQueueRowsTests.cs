using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Dock;
using Yaat.Sim;

namespace Yaat.Client.Tests.Views.Scope;

/// <summary>
/// The pilot lens's RPO queue rows: the check-in exceptions first, then every queued pilot command waiting on its trigger,
/// each group by callsign.
/// </summary>
public class RpoQueueRowsTests
{
    private static readonly ScopeViewer EramViewer = new(new ActingPosition("OAK_44_CTR", "ZOA", "C44"), "ZOA", "44");

    private static readonly PendingPilotRequestDto Release = new("Release", "Pending", "ready for release", 1.0, 1.0);

    private static AircraftModel Unowned(string callsign, string pendingCommands)
    {
        AircraftModel ac = ScopeTestAircraft.CreateModel();
        ac.Callsign = callsign;
        ac.PendingCommands = pendingCommands;
        return ac;
    }

    private static AircraftModel OwnedByViewer(string callsign, bool contact, string pendingCommands)
    {
        AircraftModel ac = Unowned(callsign, pendingCommands);
        ac.Owner = "OAK_44_CTR";
        ac.OwnerFacilityId = "ZOA";
        ac.OwnerSectorCode = "C44";
        ac.HasMadeInitialContact = contact;
        return ac;
    }

    private static IEnumerable<string> Texts(IReadOnlyList<RpoQueueRow> rows) => rows.Select(r => r.Text);

    [Fact]
    public void NoAircraft_GiveNoRows() => Assert.Empty(RpoQueueRows.Build([], EramViewer));

    [Fact]
    public void AQueuedCommand_IsOneWaitRow_WithoutItsIndex_AndTheActiveBlockIsLeftOut()
    {
        IReadOnlyList<RpoQueueRow> rows = RpoQueueRows.Build([Unowned("UAL1549", "[Active] FH 270\n[1] AT MENLO DM 040")], EramViewer);

        Assert.Equal([new RpoQueueRow("UAL1549", "UAL1549 AT MENLO DM 040 wait")], rows);
    }

    [Fact]
    public void CheckInRows_ComeFirstByCallsign_ThenQueuedRowsByCallsign()
    {
        AircraftModel calling = Unowned("DAL300", "");
        calling.PendingPilotRequest = Release;

        IReadOnlyList<RpoQueueRow> rows = RpoQueueRows.Build(
            [
                Unowned("UAL1549", "[1] DM 040 @MENLO"),
                OwnedByViewer("SKW5512", contact: false, ""),
                Unowned("AAL22", "[1] CM 050\n[2] DCT SJC"),
                calling,
            ],
            EramViewer
        );

        Assert.Equal(
            [
                "DAL300 calling RELEASE !",
                "SKW5512 not checked in (no VCI) !",
                "AAL22 CM 050 wait",
                "AAL22 DCT SJC wait",
                "UAL1549 DM 040 @MENLO wait",
            ],
            Texts(rows)
        );
        Assert.Equal(["DAL300", "SKW5512", "AAL22", "AAL22", "UAL1549"], rows.Select(r => r.Callsign));
    }

    [Fact]
    public void AnAircraftWithBoth_HasItsCheckInRowFirst_AndItsQueuedRowsInTheSecondGroup()
    {
        IReadOnlyList<RpoQueueRow> rows = RpoQueueRows.Build(
            [OwnedByViewer("UAL1", contact: false, "[1] FH 270"), Unowned("AAL5", "[1] CM 050")],
            EramViewer
        );

        Assert.Equal(["UAL1 not checked in (no VCI) !", "AAL5 CM 050 wait", "UAL1 FH 270 wait"], Texts(rows));
    }

    [Fact]
    public void VciLit_WithNoContact_IsTheVciLitRow()
    {
        AircraftModel ac = OwnedByViewer("SKW5512", contact: false, "");
        ac.EramOnFrequencySectorIds = ["44"];

        Assert.Equal(["SKW5512 not checked in (VCI lit) !"], Texts(RpoQueueRows.Build([ac], EramViewer)));
    }

    [Fact]
    public void ContactMade_OnAnOwnedEramTrack_WithTheVciNotLit_IsTheVciNotSetRow() =>
        Assert.Equal(["UAL1 checked in, VCI not set"], Texts(RpoQueueRows.Build([OwnedByViewer("UAL1", contact: true, "")], EramViewer)));

    [Fact]
    public void AnAircraftWithNoCheckInStateAndNothingQueued_HasNoRow() =>
        Assert.Empty(RpoQueueRows.Build([Unowned("UAL1", "[Active] FH 270")], EramViewer));
}
