using Xunit;
using Yaat.Client.ContextMenus;
using Yaat.Client.Models;
using Yaat.Sim.Commands;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="RelativeTraffic"/> — the gating logic for the selected→right-clicked traffic context-menu
/// items (RTIS / FOLLOW in the air, GW / FOLLOWG on the ground). Builds <see cref="AircraftModel"/> instances
/// directly — no simulation, no recording replay.
/// </summary>
public class RelativeTrafficTests
{
    private static AircraftModel Ac(string callsign, bool onGround, string? lastReported, bool liveTraffic) =>
        new()
        {
            Callsign = callsign,
            IsOnGround = onGround,
            LastReportedTrafficCallsign = lastReported,
            IsLiveTraffic = liveTraffic,
        };

    private static MenuContext Context(AircraftModel? previousSelection, string callsign = "N172SP") =>
        new(new MenuClick(callsign, previousSelection, null, []), new MenuSession("AB", false, VfrCommandsForIfr.None, QuickCommandDefaults.For));

    // --- HasRelativeContext -----------------------------------------------------

    [Fact]
    public void HasRelativeContext_NullSelected_False() => Assert.False(RelativeTraffic.HasRelativeContext(null, "N172SP"));

    [Fact]
    public void HasRelativeContext_SameCallsign_False() =>
        Assert.False(RelativeTraffic.HasRelativeContext(Ac("N172SP", false, null, false), "N172SP"));

    [Fact]
    public void HasRelativeContext_SameCallsignCaseInsensitive_False() =>
        Assert.False(RelativeTraffic.HasRelativeContext(Ac("n172sp", false, null, false), "N172SP"));

    [Fact]
    public void HasRelativeContext_DifferentCallsign_True() =>
        Assert.True(RelativeTraffic.HasRelativeContext(Ac("N436MS", false, null, false), "N172SP"));

    // --- ShouldOfferFollow ------------------------------------------------------

    [Fact]
    public void ShouldOfferFollow_AirborneAndReportedThatTraffic_True() =>
        Assert.True(RelativeTraffic.ShouldOfferFollow(Ac("N436MS", false, "N172SP", false), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_CaseInsensitiveMatch_True() =>
        Assert.True(RelativeTraffic.ShouldOfferFollow(Ac("N436MS", false, "n172sp", false), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_NoTrafficReported_False() =>
        Assert.False(RelativeTraffic.ShouldOfferFollow(Ac("N436MS", false, null, false), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_ReportedDifferentTraffic_False() =>
        Assert.False(RelativeTraffic.ShouldOfferFollow(Ac("N436MS", false, "N999XX", false), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_OnGround_False() => Assert.False(RelativeTraffic.ShouldOfferFollow(Ac("N436MS", true, "N172SP", false), "N172SP"));

    // --- ShouldOfferGroundActions -----------------------------------------------

    [Fact]
    public void ShouldOfferGroundActions_BothOnGround_True() =>
        Assert.True(RelativeTraffic.ShouldOfferGroundActions(Ac("N436MS", true, null, false), Ac("N172SP", true, null, false)));

    [Fact]
    public void ShouldOfferGroundActions_SelectedAirborne_False() =>
        Assert.False(RelativeTraffic.ShouldOfferGroundActions(Ac("N436MS", false, null, false), Ac("N172SP", true, null, false)));

    [Fact]
    public void ShouldOfferGroundActions_TargetAirborne_False() =>
        Assert.False(RelativeTraffic.ShouldOfferGroundActions(Ac("N436MS", true, null, false), Ac("N172SP", false, null, false)));

    // --- OffersAirborneRelative -------------------------------------------------

    [Fact]
    public void AirbornePair_RequiresBothAirborne()
    {
        AircraftModel selected = Ac("N436MS", false, null, false);

        Assert.False(RelativeTraffic.OffersAirborneRelative(Ac("N172SP", true, null, false), Context(selected)));
        Assert.True(RelativeTraffic.OffersAirborneRelative(Ac("N172SP", false, null, false), Context(selected)));
    }

    [Fact]
    public void AirbornePair_NoPreviousSelection_False() =>
        Assert.False(RelativeTraffic.OffersAirborneRelative(Ac("N172SP", false, null, false), Context(null)));

    [Fact]
    public void AirborneFollow_OnlyAfterTrafficReportedInSight()
    {
        AircraftModel selected = Ac("N436MS", false, "N172SP", false);

        Assert.True(RelativeTraffic.OffersAirborneFollow(Ac("N172SP", false, null, false), Context(selected)));
        Assert.False(RelativeTraffic.OffersAirborneFollow(Ac("N172SP", false, null, false), Context(Ac("N436MS", false, null, false))));
    }

    // --- OffersGroundRelative ---------------------------------------------------

    [Fact]
    public void GroundPair_BothOnGround_True() =>
        Assert.True(RelativeTraffic.OffersGroundRelative(Ac("N172SP", true, null, false), Context(Ac("N436MS", true, null, false))));

    [Fact]
    public void GroundPair_NoPreviousSelection_False() =>
        Assert.False(RelativeTraffic.OffersGroundRelative(Ac("N172SP", true, null, false), Context(null)));

    // --- Live traffic -----------------------------------------------------------

    [Fact]
    public void Relative_SelectedSurfaceShadow_OffersNothing()
    {
        AircraftModel shadow = Ac("SWA9", true, null, true);

        Assert.False(RelativeTraffic.OffersGroundRelative(Ac("N172SP", true, null, false), Context(shadow)));
        Assert.False(RelativeTraffic.OffersAirborneRelative(Ac("N172SP", false, null, false), Context(shadow)));
    }
}
