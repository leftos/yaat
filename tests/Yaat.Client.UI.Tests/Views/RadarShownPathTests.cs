using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;
using Yaat.Sim.Data;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Radar's "Show nav route" draws what the server sends: the route's fixes and the server's shapes, and no approach
/// line of its own re-derived from CIFP.
/// </summary>
public class RadarShownPathTests
{
    [Fact]
    public void ExpectedApproach_WithNoServerFinalShape_ShowsOnlyTheRoute()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var ac = new AircraftModel
        {
            Callsign = "UAL1",
            Destination = "OAK",
            ExpectedApproach = "I28R",
            NavRouteFixes = [new NavRouteFixDto("ALPHA", 37.9, -122.4, null, null)],
        };
        var vm = new RadarViewModel(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);
        vm.SetAircraftLookup(callsign => callsign == ac.Callsign ? ac : null);
        vm.SetNavDbReady();

        vm.ToggleShowPath(ac.Callsign);

        ShownPathEntry entry = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShownPathEntry>>(vm.ShownPaths));
        Assert.Null(entry.Tail);
        Assert.Equal(["ALPHA"], entry.Waypoints.Select(w => w.ResolvedName));
        Assert.Null(vm.ShownShapes);
    }

    /// <summary>
    /// A vectored aircraft's heading tail starts where the aircraft is now on every refresh, also when the cached entry is
    /// reused, as the Scope's does.
    /// </summary>
    [Fact]
    public void VectoredAircraft_HeadingTailFollowsTheAircraftAcrossRefreshes()
    {
        using IDisposable navScope = NavigationDatabase.ScopedOverride(MenuGoldenFixtures.EnsureNavData());
        var ac = new AircraftModel
        {
            Callsign = "UAL2",
            Position = new LatLon(37.6, -122.4),
            AssignedHeading = new MagneticHeading(250.0),
        };
        var vm = new RadarViewModel(new ServerConnection(), new VideoMapService(), (_, _, _) => Task.CompletedTask);
        vm.SetAircraftLookup(callsign => callsign == ac.Callsign ? ac : null);
        vm.SetNavDbReady();
        vm.ToggleShowPath(ac.Callsign);

        ac.Position = new LatLon(37.62, -122.43);
        vm.RefreshShownPaths();

        ShownPathEntry entry = Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<ShownPathEntry>>(vm.ShownPaths));
        VectorTail tail = Assert.IsType<VectorTail>(entry.Tail);
        Assert.Equal(37.62, tail.FromLat, 6);
        Assert.Equal(-122.43, tail.FromLon, 6);
        Assert.Equal(250.0, tail.HeadingMag, 3);
    }
}
