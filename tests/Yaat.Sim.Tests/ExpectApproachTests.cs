using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class ExpectApproachTests
{
    public ExpectApproachTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static AircraftState MakeAircraft(string destination = "OAK")
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            TrueHeading = new TrueHeading(280),
            Altitude = 5000,
            Position = new LatLon(37.75, -122.35),
            FlightPlan = new AircraftFlightPlan { Destination = destination },
        };
    }

    private static RunwayInfo MakeRunway()
    {
        return TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            endLat: 37.73,
            endLon: -122.27,
            heading: 280,
            elevationFt: 9
        );
    }

    private static NavigationDatabase MakeNavDb()
    {
        var procedure = new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );

        return TestNavDbFactory.WithRunwayAndApproaches(MakeRunway(), [procedure]);
    }

    [Fact]
    public void Eapp_SetsExpectedApproach()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.Approach.Expected);
    }

    [Fact]
    public void Eapp_ReturnsConfirmationMessage()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ExpectApproachCommand("I28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Contains("Expecting", result.Message);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Eapp_WithExplicitAirport_SetsExpectedApproach()
    {
        var aircraft = MakeAircraft(destination: "SFO");
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // Explicit airport overrides destination
        var cmd = new ExpectApproachCommand("ILS28R", "OAK");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.Approach.Expected);
    }

    [Fact]
    public void Eapp_UnknownApproach_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ExpectApproachCommand("VOR99", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    [Fact]
    public void Eapp_OverwritesPreviousExpectedApproach()
    {
        var aircraft = MakeAircraft();
        aircraft.Approach.Expected = "V28L";
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.Approach.Expected);
    }

    [Fact]
    public void Eapp_WithActiveStar_ExtendsRouteWithRunwayTransition()
    {
        // Aircraft already on the WNDSR2 STAR (joined at WEBRR, no runway transition yet).
        // EAPP I30 must extend the route with the RW30 transition fixes so the published
        // FM vector arrow at CRSEN can render on the radar overlay.
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        aircraft.Procedure.ActiveStarId = "WNDSR2";
        // Pre-load the common-leg fixes as if JARR WNDSR2 WEBRR had already run without a
        // destination runway set.
        var webrr = navDb.GetFixPosition("WEBRR")!.Value;
        var boyys = navDb.GetFixPosition("BOYYS")!.Value;
        var hopta = navDb.GetFixPosition("HOPTA")!.Value;
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "WEBRR", Position = new LatLon(webrr.Lat, webrr.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "BOYYS", Position = new LatLon(boyys.Lat, boyys.Lon) });
        aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = "HOPTA", Position = new LatLon(hopta.Lat, hopta.Lon) });

        var cmd = new ExpectApproachCommand("I30", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"EAPP I30 should succeed. Got: {result.Message}");
        Assert.Equal("30", aircraft.Procedure.DestinationRunway);

        // WNDSR2 RW30 transition is HOPTA → ALLXX → CRSEN. HOPTA was already in the route;
        // ALLXX and CRSEN should now be appended.
        var names = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        Assert.Contains("ALLXX", names);
        Assert.Contains("CRSEN", names);
        Assert.Equal(names.IndexOf("HOPTA") + 1, names.IndexOf("ALLXX"));
    }

    [Fact]
    public void Eapp_ChangingRunway_RemovesStaleRunwayTransitionFixes()
    {
        // WNDSR2 RW28B ends at AAAME; RW30 ends at ALLXX/CRSEN. If the route already
        // contains the 28B transition and the controller issues EAPP I30, AAAME must not
        // remain — otherwise the pilot flies the wrong path after the runway change.
        TestVnasData.EnsureInitialized();
        var navDb = TestVnasData.NavigationDb;
        if (navDb is null)
        {
            return;
        }
        NavigationDatabase.SetInstance(navDb);

        var aircraft = MakeAircraft();
        aircraft.Procedure.ActiveStarId = "WNDSR2";
        aircraft.Procedure.DestinationRunway = "28B";

        foreach (var name in new[] { "WEBRR", "BOYYS", "HOPTA", "AAAME" })
        {
            var pos = navDb.GetFixPosition(name)!.Value;
            aircraft.Targets.NavigationRoute.Add(new NavigationTarget { Name = name, Position = new LatLon(pos.Lat, pos.Lon) });
        }

        var cmd = new ExpectApproachCommand("I30", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success, $"EAPP I30 should succeed. Got: {result.Message}");
        Assert.Equal("30", aircraft.Procedure.DestinationRunway);

        var names = aircraft.Targets.NavigationRoute.Select(t => t.Name).ToList();
        Assert.DoesNotContain("AAAME", names);
        Assert.Contains("ALLXX", names);
        Assert.Contains("CRSEN", names);
    }

    [Fact]
    public void Eapp_SetsDestinationRunwayFromResolvedApproach()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        Assert.Null(aircraft.Procedure.DestinationRunway);

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("28R", aircraft.Procedure.DestinationRunway);
    }

    [Fact]
    public void Eapp_ResolvesShorthandId()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();
        using var _ = NavigationDatabase.ScopedOverride(navDb);

        // "ILS28R" should resolve to "I28R"
        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.Approach.Expected);
    }
}
