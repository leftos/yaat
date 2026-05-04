using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for the predicate that backs <c>MainViewModel.AircraftView.Filter</c>.
/// STARS unsupported data blocks created by CRC <c>DA</c>/<c>VP</c> typing must be
/// hidden from the operator-facing Aircraft List even though they are broadcast
/// to the client (so STARS scope and flight strips can render them).
/// </summary>
public class AircraftViewFilterTests
{
    private static AircraftModel Model(bool isUnsupported = false, string status = "Active", string callsign = "N123AB")
    {
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "C172",
            IsUnsupported = isUnsupported,
            Status = status,
        };
    }

    [Fact]
    public void Filter_HidesUnsupportedGhostTracks_RegardlessOfShowOnlyActive()
    {
        var ac = Model(isUnsupported: true);
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: ""));
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: true, filter: ""));
    }

    [Fact]
    public void Filter_IncludesAircraftWhenIsUnsupportedFalse()
    {
        var ac = Model(isUnsupported: false);
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: ""));
    }

    [Fact]
    public void Filter_HidesDelayedOnlyWhenShowOnlyActive()
    {
        var ac = Model(status: "Delayed (45s)");
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: ""));
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: true, filter: ""));
    }

    [Fact]
    public void Filter_TextSearchStillAppliesToVisibleAircraft()
    {
        var ac = Model(callsign: "UAL238");
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: "UAL"));
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: "DAL"));
    }

    [Fact]
    public void UnsupportedGhostStillProducesNoAltitudeAsgnSmartStatus()
    {
        // Sanity-check that the underlying SmartStatus computation is unchanged —
        // the filter is the layer that hides the row, not a SmartStatus rewrite.
        var ac = new AircraftModel
        {
            Callsign = "*T",
            AircraftType = "",
            IsUnsupported = true,
        };
        ac.ComputeSmartStatus();
        Assert.Equal("No altitude asgn", ac.SmartStatus);
    }
}
