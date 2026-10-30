using Xunit;
using Yaat.Client.Views.Map;

namespace Yaat.Client.UI.Tests.Views;

// The picker's hovered-first rule: the target the hover label names is option 1, every other target keeps its order.
public class RightClickTargetOrderTests
{
    private static readonly RightClickTarget Ual = RightClickTarget.ForAircraft("UAL238", "B738", viaDataBlock: false);
    private static readonly RightClickTarget Swa = RightClickTarget.ForAircraft("SWA12", "B737", viaDataBlock: true);
    private static readonly RightClickTarget Stand = new("Parking A5", null, 7, false);
    private static readonly RightClickTarget Spot = new("Spot 3", null, 9, false);

    [Fact]
    public void HoveredFirst_HoveredInTheList_MovesItFirstAndKeepsTheOthersOrder()
    {
        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual, Swa, Spot, Stand], Spot);

        Assert.Equal([Spot, Ual, Swa, Stand], ordered);
    }

    // The match is by callsign, not by the whole entry: the listed entry is kept, with its datablock flag.
    [Fact]
    public void HoveredFirst_SameAircraftHitAnotherWay_MovesTheListedEntry()
    {
        var hovered = RightClickTarget.ForAircraft("SWA12", "B737", viaDataBlock: false);

        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual, Swa], hovered);

        Assert.Equal([Swa, Ual], ordered);
        Assert.True(ordered[0].ViaDataBlock);
    }

    [Fact]
    public void HoveredFirst_HoveredAbsent_InsertsItFirst()
    {
        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual, Swa], Stand);

        Assert.Equal([Stand, Ual, Swa], ordered);
    }

    [Fact]
    public void HoveredFirst_NoHovered_LeavesTheListUnchanged()
    {
        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual, Stand, Swa], null);

        Assert.Equal([Ual, Stand, Swa], ordered);
    }

    [Fact]
    public void HoveredFirst_SingleItemThatIsHovered_StaysOneItem()
    {
        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Stand], Stand);

        Assert.Equal([Stand], ordered);
    }

    // A target carrying both a callsign and a node id matches by callsign both ways round: it never matches a bare node
    // target with the same id, so a hovered stand neither swallows it nor is listed twice.
    [Fact]
    public void HoveredFirst_TargetWithCallsignAndNodeId_MatchesSymmetricallyByCallsign()
    {
        var both = new RightClickTarget("UAL238 at A5", "UAL238", 7, false);

        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual, both], Stand);

        Assert.False(Stand.IsSameTargetAs(both));
        Assert.False(both.IsSameTargetAs(Stand));
        Assert.True(both.IsSameTargetAs(Ual));
        Assert.True(Ual.IsSameTargetAs(both));
        Assert.Equal([Stand, Ual, both], ordered);
    }

    // A node and an aircraft never match, even when neither carries the other's key.
    [Fact]
    public void HoveredFirst_NodeHoveredOverAircraftOnlyList_IsInserted()
    {
        IReadOnlyList<RightClickTarget> ordered = RightClickTargetOrder.HoveredFirst([Ual], Stand);

        Assert.Equal([Stand, Ual], ordered);
    }
}
