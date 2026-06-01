using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Views;

namespace Yaat.Client.Tests;

// Covers HoldShortMenuHelper.HeldRunway — the shared resolver the ground-map and
// track-list right-click menus use to pick the runway an aircraft is holding short of.
// Regression for N784ME holding short of 15/33 but offered crossings for 28R (its
// departure runway): the held runway must come from the phase, never from AssignedRunway
// when the phase carries a runway.
public class HoldShortMenuHelperTests
{
    [Fact]
    public void HeldRunway_UsesPhaseRunway_NotAssignedRunway()
    {
        var ac = new AircraftModel { Callsign = "N784ME", AssignedRunway = "28R" };

        Assert.Equal("15", HoldShortMenuHelper.HeldRunway("Holding Short 15/33", ac));
    }

    [Fact]
    public void HeldRunway_CompoundId_ReturnsFirstEnd()
    {
        var ac = new AircraftModel { Callsign = "X" };

        Assert.Equal("28L", HoldShortMenuHelper.HeldRunway("Holding Short 28L/10R", ac));
    }

    [Fact]
    public void HeldRunway_NoRunwayInPhase_FallsBackToAssignedRunway()
    {
        var ac = new AircraftModel { Callsign = "X", AssignedRunway = "10R" };

        Assert.Equal("10R", HoldShortMenuHelper.HeldRunway("Holding Short", ac));
    }

    [Fact]
    public void HeldRunway_NoRunwayAndNoAssigned_ReturnsNull()
    {
        var ac = new AircraftModel { Callsign = "X" };

        Assert.Null(HoldShortMenuHelper.HeldRunway("Holding Short", ac));
    }
}
