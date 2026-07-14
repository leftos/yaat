using Xunit;
using Yaat.Client.Models;
using Yaat.Client.ViewModels;
using static Yaat.Sim.AircraftStatusDescriber;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests for the predicate that backs <c>MainViewModel.AircraftView.Filter</c>.
/// Phantom STARS data blocks created by CRC <c>DA</c>/<c>VP</c> typing (callsigns
/// with no real aircraft body) must be hidden from the operator-facing Aircraft List
/// even though they are broadcast to the client. Ghost overlays attached to real
/// scenario aircraft (AID+slew) must remain visible — the aircraft is still flying.
/// </summary>
public class AircraftViewFilterTests
{
    private static AircraftModel Model(bool isUnsupported = false, bool isGhostOverlay = false, string status = "Active", string callsign = "N123AB")
    {
        return new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "C172",
            IsUnsupported = isUnsupported,
            IsGhostOverlay = isGhostOverlay,
            Status = status,
        };
    }

    [Fact]
    public void Filter_HidesUnsupportedPhantoms_RegardlessOfShowOnlyActive()
    {
        var ac = Model(isUnsupported: true);
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: ""));
        Assert.False(MainViewModel.IsAircraftVisible(ac, showOnlyActive: true, filter: ""));
    }

    [Fact]
    public void Filter_IncludesGhostOverlaysOnRealAircraft_RegardlessOfShowOnlyActive()
    {
        // Real scenario aircraft with an AID+slew ghost overlay attached. STARS shows
        // the pinned ghost position; the YAAT Aircraft List must keep the row visible
        // so the operator can still track the underlying aircraft.
        var ac = Model(isUnsupported: true, isGhostOverlay: true);
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: ""));
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: true, filter: ""));
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
        // Sanity-check that the underlying status projection is unchanged — the filter is the layer
        // that hides the row, not a status rewrite. An unsupported phantom is airborne with no phase,
        // SID, altitude, or route, which the describer reports with a leading "No altitude asgn"
        // warning (now prepended to the normal status rather than replacing it).
        Assert.StartsWith("No altitude asgn", Describe(new AircraftStatusView { IsOnGround = false }).Text);
    }

    [Fact]
    public void Filter_TextSearch_MatchesPrependedWarningStatus()
    {
        // Warnings prepend to (not replace) the normal status, so both the warning word and the
        // underlying activity stay searchable via the Aircraft List filter's SmartStatus match.
        var ac = Model();
        ac.SmartStatus = "No landing clnc · Final 28R";
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: "landing"));
        Assert.True(MainViewModel.IsAircraftVisible(ac, showOnlyActive: false, filter: "final"));
    }
}
