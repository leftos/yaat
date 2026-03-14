using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

public class ExpectApproachTests
{
    private static readonly ILogger Logger = NullLogger.Instance;

    private static AircraftState MakeAircraft(string destination = "OAK")
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = 280,
            Altitude = 5000,
            Latitude = 37.75,
            Longitude = -122.35,
            Destination = destination,
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

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_ReturnsConfirmationMessage()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();

        var cmd = new ExpectApproachCommand("I28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Contains("Expecting", result.Message);
        Assert.Contains("I28R", result.Message);
    }

    [Fact]
    public void Eapp_WithExplicitAirport_SetsExpectedApproach()
    {
        var aircraft = MakeAircraft(destination: "SFO");
        var navDb = MakeNavDb();

        // Explicit airport overrides destination
        var cmd = new ExpectApproachCommand("ILS28R", "OAK");
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_UnknownApproach_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();

        var cmd = new ExpectApproachCommand("VOR99", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.False(result.Success);
        Assert.Contains("Unknown approach", result.Message);
    }

    [Fact]
    public void Eapp_NoApproachLookup_ReturnsError()
    {
        var aircraft = MakeAircraft();
        var navDb = TestNavDbFactory.WithRunways(MakeRunway());

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.False(result.Success);
        // Fails with "Unknown approach" when navDb has no approaches loaded
        Assert.False(string.IsNullOrEmpty(result.Message));
    }

    [Fact]
    public void Eapp_OverwritesPreviousExpectedApproach()
    {
        var aircraft = MakeAircraft();
        aircraft.ExpectedApproach = "V28L";
        var navDb = MakeNavDb();

        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }

    [Fact]
    public void Eapp_ResolvesShorthandId()
    {
        var aircraft = MakeAircraft();
        var navDb = MakeNavDb();

        // "ILS28R" should resolve to "I28R"
        var cmd = new ExpectApproachCommand("ILS28R", null);
        var result = CommandDispatcher.Dispatch(cmd, aircraft, navDb, null, Random.Shared, true);

        Assert.True(result.Success);
        Assert.Equal("I28R", aircraft.ExpectedApproach);
    }
}
