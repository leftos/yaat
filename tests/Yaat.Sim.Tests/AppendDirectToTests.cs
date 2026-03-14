using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

public class AppendDirectToTests
{
    private static AircraftState CreateAircraft(string route = "")
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Latitude = 37.62,
            Longitude = -122.38,
            Heading = 280,
            Altitude = 10000,
            Route = route,
        };
    }

    private static readonly ILogger Logger = NullLogger.Instance;

    [Fact]
    public void Adct_NoExistingRoute_BehavesLikeDct()
    {
        var aircraft = CreateAircraft();
        var navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Contains("Proceed direct SUNOL", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_AppendsToEnd()
    {
        var aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "MOVDD",
                Latitude = 37.6,
                Longitude = -122.0,
            }
        );
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "BRIXX",
                Latitude = 37.7,
                Longitude = -121.9,
            }
        );

        var navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("BRIXX", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Contains("Then direct SUNOL", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_PreservesOriginalFixes()
    {
        var aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "MOVDD",
                Latitude = 37.6,
                Longitude = -122.0,
            }
        );

        var navDb = TestNavDbFactory.WithFixes(("FIX1", 37.5, -121.8), ("FIX2", 37.4, -121.7));
        var cmd = new AppendDirectToCommand([new ResolvedFix("FIX1", 37.5, -121.8), new ResolvedFix("FIX2", 37.4, -121.7)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("FIX1", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("FIX2", aircraft.Targets.NavigationRoute[2].Name);
    }

    [Fact]
    public void Adct_NoExistingRoute_ChainsFiledRoute()
    {
        var aircraft = CreateAircraft("SUNOL MODESTO OXNARD");
        var navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("MODESTO", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("OXNARD", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Contains("then filed route", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_ChainsFiledRoute()
    {
        var aircraft = CreateAircraft("MOVDD SUNOL MODESTO");
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "MOVDD",
                Latitude = 37.6,
                Longitude = -122.0,
            }
        );

        var navDb = TestNavDbFactory.WithFixes(("MOVDD", 37.6, -122.0), ("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("MODESTO", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Contains("then filed route", result.Message);
    }

    [Fact]
    public void Dct_ClearsExistingRoute_UnlikeAdct()
    {
        var aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "MOVDD",
                Latitude = 37.6,
                Longitude = -122.0,
            }
        );

        var navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)]);

        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
    }
}
