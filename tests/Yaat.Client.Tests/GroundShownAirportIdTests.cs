using Xunit;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for <see cref="MainViewModel.ResolveGroundShownAirportId"/> — the focus rule behind
/// issue #169. A ground aircraft's bubble surfaces on the radar whenever the ground view isn't
/// presenting that airport to the user, which includes the ground view being docked but a
/// different tab (Aircraft List, Strips, Radar) being in focus.
/// </summary>
public class GroundShownAirportIdTests
{
    private const int GroundTab = 1;

    [Fact]
    public void DockedGroundViewSelected_ReturnsAirport()
    {
        // Ground tab is the in-focus docked tab → the ground view is presenting OAK.
        var result = MainViewModel.ResolveGroundShownAirportId(
            groundViewPoppedOut: false,
            selectedTabIndex: GroundTab,
            groundTabIndex: GroundTab,
            "OAK"
        );
        Assert.Equal("OAK", result);
    }

    [Theory]
    [InlineData(0)] // Aircraft List
    [InlineData(2)] // Radar
    [InlineData(3)] // Strips
    public void DockedGroundViewNotInFocus_ReturnsNull(int selectedTabIndex)
    {
        // Ground view docked but another tab is in focus → not shown → bubbles surface on radar.
        var result = MainViewModel.ResolveGroundShownAirportId(groundViewPoppedOut: false, selectedTabIndex, groundTabIndex: GroundTab, "OAK");
        Assert.Null(result);
    }

    [Fact]
    public void PoppedOutGroundView_ReturnsAirport_RegardlessOfSelectedTab()
    {
        var result = MainViewModel.ResolveGroundShownAirportId(groundViewPoppedOut: true, selectedTabIndex: 0, groundTabIndex: GroundTab, "OAK");
        Assert.Equal("OAK", result);
    }

    [Fact]
    public void NoLayoutLoaded_ReturnsNull()
    {
        var result = MainViewModel.ResolveGroundShownAirportId(
            groundViewPoppedOut: false,
            selectedTabIndex: GroundTab,
            groundTabIndex: GroundTab,
            null
        );
        Assert.Null(result);
    }
}
