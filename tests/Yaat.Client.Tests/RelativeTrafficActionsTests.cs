using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="RelativeTrafficActions"/> — the gating logic for the
/// selected→right-clicked traffic context-menu items (RTIS / FOLLOW in radar,
/// GIVEWAY / FOLLOWG on the ground). Builds <see cref="AircraftModel"/> instances
/// directly — no simulation, no recording replay.
/// </summary>
public class RelativeTrafficActionsTests
{
    private static AircraftModel Ac(string callsign, bool onGround = false, string? lastReported = null) =>
        new()
        {
            Callsign = callsign,
            IsOnGround = onGround,
            LastReportedTrafficCallsign = lastReported,
        };

    // --- HasRelativeContext -----------------------------------------------------

    [Fact]
    public void HasRelativeContext_NullSelected_False() => Assert.False(RelativeTrafficActions.HasRelativeContext(null, "N172SP"));

    [Fact]
    public void HasRelativeContext_SameCallsign_False() => Assert.False(RelativeTrafficActions.HasRelativeContext(Ac("N172SP"), "N172SP"));

    [Fact]
    public void HasRelativeContext_SameCallsignCaseInsensitive_False() =>
        Assert.False(RelativeTrafficActions.HasRelativeContext(Ac("n172sp"), "N172SP"));

    [Fact]
    public void HasRelativeContext_DifferentCallsign_True() => Assert.True(RelativeTrafficActions.HasRelativeContext(Ac("N436MS"), "N172SP"));

    // --- ShouldOfferFollow ------------------------------------------------------

    [Fact]
    public void ShouldOfferFollow_AirborneAndReportedThatTraffic_True() =>
        Assert.True(RelativeTrafficActions.ShouldOfferFollow(Ac("N436MS", onGround: false, lastReported: "N172SP"), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_CaseInsensitiveMatch_True() =>
        Assert.True(RelativeTrafficActions.ShouldOfferFollow(Ac("N436MS", onGround: false, lastReported: "n172sp"), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_NoTrafficReported_False() =>
        Assert.False(RelativeTrafficActions.ShouldOfferFollow(Ac("N436MS", onGround: false, lastReported: null), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_ReportedDifferentTraffic_False() =>
        Assert.False(RelativeTrafficActions.ShouldOfferFollow(Ac("N436MS", onGround: false, lastReported: "N999XX"), "N172SP"));

    [Fact]
    public void ShouldOfferFollow_OnGround_False() =>
        Assert.False(RelativeTrafficActions.ShouldOfferFollow(Ac("N436MS", onGround: true, lastReported: "N172SP"), "N172SP"));

    // --- ShouldOfferGroundActions -----------------------------------------------

    [Fact]
    public void ShouldOfferGroundActions_BothOnGround_True() =>
        Assert.True(RelativeTrafficActions.ShouldOfferGroundActions(Ac("N436MS", onGround: true), Ac("N172SP", onGround: true)));

    [Fact]
    public void ShouldOfferGroundActions_SelectedAirborne_False() =>
        Assert.False(RelativeTrafficActions.ShouldOfferGroundActions(Ac("N436MS", onGround: false), Ac("N172SP", onGround: true)));

    [Fact]
    public void ShouldOfferGroundActions_TargetAirborne_False() =>
        Assert.False(RelativeTrafficActions.ShouldOfferGroundActions(Ac("N436MS", onGround: true), Ac("N172SP", onGround: false)));
}
