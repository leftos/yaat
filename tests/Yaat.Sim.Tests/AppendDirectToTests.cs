using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class AppendDirectToTests
{
    public AppendDirectToTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState CreateAircraft(string route = "")
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 10000,
            FlightPlan = new AircraftFlightPlan { Route = route },
        };
    }

    [Fact]
    public void Adct_NoExistingRoute_BehavesLikeDct()
    {
        AircraftState aircraft = CreateAircraft();
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("Proceed direct SUNOL; route SUNOL", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_AppendsToEnd()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "MOVDD", Position = new LatLon(37.6, -122.0) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "BRIXX", Position = new LatLon(37.7, -121.9) });

        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("BRIXX", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Equal("Then direct SUNOL; route MOVDD BRIXX SUNOL", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_PreservesOriginalFixes()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "MOVDD", Position = new LatLon(37.6, -122.0) });

        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("FIX1", 37.5, -121.8), ("FIX2", 37.4, -121.7));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("FIX1", 37.5, -121.8), new ResolvedFix("FIX2", 37.4, -121.7)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("FIX1", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("FIX2", aircraft.Targets.NavigationRoute[2].Name);
    }

    [Fact]
    public void Adct_NoExistingRoute_ChainsFiledRoute()
    {
        AircraftState aircraft = CreateAircraft("SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0), ("OXNARD", 34.2, -119.2));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("MODESTO", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("OXNARD", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Equal("Proceed direct SUNOL, then filed route; route SUNOL MODESTO OXNARD", result.Message);
    }

    [Fact]
    public void Adct_WithExistingRoute_ChainsFiledRoute()
    {
        AircraftState aircraft = CreateAircraft("MOVDD SUNOL MODESTO");
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "MOVDD", Position = new LatLon(37.6, -122.0) });

        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("MOVDD", 37.6, -122.0), ("SUNOL", 37.5, -121.8), ("MODESTO", 37.6, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal(3, aircraft.Targets.NavigationRoute.Count);
        Assert.Equal("MOVDD", aircraft.Targets.NavigationRoute[0].Name);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[1].Name);
        Assert.Equal("MODESTO", aircraft.Targets.NavigationRoute[2].Name);
        Assert.Equal("Then direct SUNOL, then filed route; route MOVDD SUNOL MODESTO", result.Message);
    }

    [Fact]
    public void Dct_ClearsExistingRoute_UnlikeAdct()
    {
        AircraftState aircraft = CreateAircraft();
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "MOVDD", Position = new LatLon(37.6, -122.0) });

        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Single(aircraft.Targets.NavigationRoute);
        Assert.Equal("SUNOL", aircraft.Targets.NavigationRoute[0].Name);
    }

    // Real-navdata responses (YAAT-2759): each ADCT/ADCTF success message ends with the full navigation route after the append.

    private static ResolvedFix RealFix(string name)
    {
        (double Lat, double Lon)? pos = NavigationDatabase.Instance.GetFixPosition(name);
        Assert.NotNull(pos);
        return new ResolvedFix(name, pos.Value.Lat, pos.Value.Lon);
    }

    private static AircraftState CreateAircraftFlying(string liveFix, string route = "")
    {
        AircraftState aircraft = CreateAircraft(route);
        ResolvedFix fix = RealFix(liveFix);
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = fix.Name, Position = new LatLon(fix.Lat, fix.Lon) });
        return aircraft;
    }

    private static CommandResult Dispatch(ParsedCommand cmd, AircraftState aircraft) =>
        CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

    [Fact]
    public void Adct_AfterLiveRoute_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraftFlying("ALTAM");

        CommandResult result = Dispatch(new AppendDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Then direct SUNOL; route ALTAM SUNOL", result.Message);
    }

    [Fact]
    public void Adct_ThenFiledRoute_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraftFlying("ALTAM", "SUNOL TRACY MOD");

        CommandResult result = Dispatch(new AppendDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Then direct SUNOL, then filed route; route ALTAM SUNOL TRACY MOD", result.Message);
    }

    [Fact]
    public void Adct_RouteRanOut_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraft();

        CommandResult result = Dispatch(new AppendDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Proceed direct SUNOL; route SUNOL", result.Message);
    }

    [Fact]
    public void Adctf_AfterLiveRoute_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraftFlying("ALTAM");

        CommandResult result = Dispatch(new AppendForceDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Then direct SUNOL; route ALTAM SUNOL", result.Message);
    }

    [Fact]
    public void Adctf_ThenFiledRoute_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraftFlying("ALTAM", "SUNOL TRACY MOD");

        CommandResult result = Dispatch(new AppendForceDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Then direct SUNOL, then filed route; route ALTAM SUNOL TRACY MOD", result.Message);
    }

    [Fact]
    public void Adctf_RouteRanOut_ReportsFullRoute()
    {
        AircraftState aircraft = CreateAircraft();

        CommandResult result = Dispatch(new AppendForceDirectToCommand([RealFix("SUNOL")], []), aircraft);

        Assert.True(result.Success);
        Assert.Equal("Proceed direct SUNOL; route SUNOL", result.Message);
    }
}
