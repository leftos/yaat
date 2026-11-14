using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests.Data;

/// <summary>
/// <see cref="NavigationDatabase.TryGetTerminalWaypoint"/> reads one airport's CIFP terminal waypoints (subsection C)
/// from the pinned FAACIFP18.gz, mapping an FAA id to its ICAO form. The NavData-first order is pinned by
/// <c>TerminalWaypointFixTests.NavDataFirst_WinsOnCollision</c>.
/// </summary>
public class NavigationDatabaseTerminalWaypointTests
{
    // CFBMS, a KBCT terminal waypoint that NavData lacks: N26 21 57.92 W080 11 56.18.
    private const double CfbmsLat = 26.3661;
    private const double CfbmsLon = -80.1989;

    public NavigationDatabaseTerminalWaypointTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static NavigationDatabase NavDb => TestVnasData.NavigationDb!;

    [Fact]
    public void TryGetTerminalWaypoint_KbctCfbms_ReturnsItsPosition()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("KBCT", "CFBMS", out (double Lat, double Lon) pos));

        Assert.Equal(CfbmsLat, pos.Lat, 0.01);
        Assert.Equal(CfbmsLon, pos.Lon, 0.01);
    }

    [Fact]
    public void TryGetTerminalWaypoint_FaaIdMapsToIcao()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("BCT", "CFBMS", out (double Lat, double Lon) pos));

        Assert.Equal(CfbmsLat, pos.Lat, 0.01);
        Assert.Equal(CfbmsLon, pos.Lon, 0.01);
    }

    [Fact]
    public void TryGetTerminalWaypoint_UnknownAirport_False()
    {
        Assert.False(NavDb.TryGetTerminalWaypoint("KOAK", "CFBMS", out _));
        Assert.False(NavDb.TryGetTerminalWaypoint("ZZZZ", "CFBMS", out _));
        Assert.False(NavDb.TryGetTerminalWaypoint("", "CFBMS", out _));
    }
}
