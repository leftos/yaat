using Xunit;
using Yaat.Client.Models;

namespace Yaat.Client.Tests;

/// <summary>
/// Covers <see cref="AircraftModel.HoldStatusDisplay"/> / <see cref="AircraftModel.IsHeld"/> for the
/// auto-yield badge: an auto-detected yield shows "→{target} (auto)" without marking the aircraft held,
/// and a controller hold always takes precedence over an auto-yield.
/// </summary>
public class AircraftModelHoldStatusTests
{
    [Fact]
    public void AutoYield_ShowsAutoBadge_AndIsNotHeld()
    {
        var ac = new AircraftModel { AutoYieldTarget = "SWA123" };

        Assert.Equal("→SWA123 (auto)", ac.HoldStatusDisplay);
        Assert.False(ac.IsHeld);
    }

    [Fact]
    public void ControllerGiveWay_TakesPrecedenceOverAutoYield()
    {
        var ac = new AircraftModel
        {
            HoldKind = "GiveWay",
            HoldYieldTarget = "UAL9",
            AutoYieldTarget = "SWA123",
        };

        Assert.Equal("→UAL9", ac.HoldStatusDisplay);
        Assert.True(ac.IsHeld);
    }

    [Fact]
    public void HoldPosition_TakesPrecedenceOverAutoYield()
    {
        var ac = new AircraftModel { HoldKind = "HoldPosition", AutoYieldTarget = "SWA123" };

        Assert.Equal("HOLD", ac.HoldStatusDisplay);
    }

    [Fact]
    public void NoHoldNoAutoYield_IsEmpty()
    {
        var ac = new AircraftModel();

        Assert.Equal(string.Empty, ac.HoldStatusDisplay);
        Assert.False(ac.IsHeld);
    }
}
