using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

[Collection("NavDbMutator")]
public class ProgrammedFixesTests
{
    private static readonly NavigationDatabase EmptyNavDb = NavigationDatabase.ForTesting();

    public ProgrammedFixesTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static IDisposable UseNavDb(NavigationDatabase navDb) => NavigationDatabase.ScopedOverride(navDb);

    private static AircraftState MakeAircraft(string route = "", string? destination = "OAK", string? expectedApproach = null)
    {
        return new AircraftState
        {
            Callsign = "TEST001",
            AircraftType = "B738",
            Position = new LatLon(37.62, -122.38),
            TrueHeading = new TrueHeading(280),
            Altitude = 10000,
            FlightPlan = new AircraftFlightPlan { Route = route, Destination = destination ?? "" },
            Approach = new AircraftApproachState { Expected = expectedApproach },
        };
    }

    private static CifpApproachProcedure MakeApproachProcedure()
    {
        return new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 30, null, null, null),
                new CifpLeg("RW28R", CifpPathTerminator.TF, null, null, null, CifpFixRole.MAP, 40, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );
    }

    private static NavigationDatabase MakeApproachDb()
    {
        CifpApproachProcedure procedure = MakeApproachProcedure();
        RunwayInfo runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );
        return TestNavDbFactory.WithRunwayAndApproaches(runway, [procedure]);
    }

    // --- GetProgrammedFixes ---

    [Fact]
    public void GetProgrammedFixes_RouteOnly_ReturnsRouteFixes()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        using IDisposable _ = UseNavDb(EmptyNavDb);

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.Contains("OXNARD", fixes);
        Assert.Equal(3, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_RouteWithAirwaySuffix_StripsCorrectly()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL.V25 MODESTO");
        using IDisposable _ = UseNavDb(EmptyNavDb);

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.DoesNotContain("V25", fixes);
        Assert.Equal(2, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_ExpectedApproachOnly_ReturnsApproachFixes()
    {
        AircraftState aircraft = MakeAircraft(expectedApproach: "I28R");
        using IDisposable _ = UseNavDb(MakeApproachDb());

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        // MAP (RW28R) should NOT be included — stops before MAP
        Assert.DoesNotContain("RW28R", fixes);
    }

    [Fact]
    public void GetProgrammedFixes_ActiveApproachOnly_ReturnsApproachFixes()
    {
        AircraftState aircraft = MakeAircraft();
        CifpApproachProcedure procedure = MakeApproachProcedure();

        // Simulate active approach via PhaseList
        aircraft.Phases = new PhaseList
        {
            ActiveApproach = new ApproachClearance
            {
                ApproachId = "I28R",
                AirportCode = "OAK",
                RunwayId = "28R",
                FinalApproachCourse = new TrueHeading(280),
                Procedure = procedure,
            },
        };

        using IDisposable _ = UseNavDb(EmptyNavDb);
        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        Assert.DoesNotContain("RW28R", fixes);
    }

    [Fact]
    public void GetProgrammedFixes_Combined_ReturnsUnion()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO", expectedApproach: "I28R");
        using IDisposable _ = UseNavDb(MakeApproachDb());

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("SUNOL", fixes);
        Assert.Contains("MODESTO", fixes);
        Assert.Contains("GROVE", fixes);
        Assert.Contains("FITKI", fixes);
        Assert.Contains("BERYL", fixes);
        Assert.Equal(5, fixes.Count);
    }

    [Fact]
    public void GetProgrammedFixes_EmptyState_ReturnsEmpty()
    {
        AircraftState aircraft = MakeAircraft(route: "", destination: null);
        using IDisposable _ = UseNavDb(EmptyNavDb);

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Empty(fixes);
    }

    [Fact]
    public void GetProgrammedFixes_CaseInsensitive()
    {
        AircraftState aircraft = MakeAircraft(route: "sunol MODESTO");
        using IDisposable _ = UseNavDb(EmptyNavDb);

        HashSet<string> fixes = aircraft.GetProgrammedFixes();

        Assert.Contains("sunol", fixes);
        Assert.Contains("SUNOL", fixes);
    }

    // --- DCT validation ---

    [Fact]
    public void Dct_ToProgrammedFix_Accepted()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_ToNonProgrammedFix_Rejected()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("not programmed", result.Message);
        Assert.Contains("DCTF", result.Message);
    }

    [Fact]
    public void Dct_ToExpectedApproachFix_Accepted()
    {
        AircraftState aircraft = MakeAircraft(expectedApproach: "I28R");
        // MakeApproachDb() has the approach for OAK I28R with GROVE as IAF; pass it as both approach lookup and fix navDb
        NavigationDatabase navDb = MakeApproachDb();
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("GROVE", 37.78, -122.35)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
    }

    [Fact]
    public void Dctf_ToNonProgrammedFix_Accepted()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new ForceDirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
    }

    [Fact]
    public void Adct_ToNonProgrammedFix_Rejected()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new AppendDirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("not programmed", result.Message);
    }

    [Fact]
    public void Dct_EmptyProgrammedSet_AllowsAnyFix()
    {
        // Aircraft with no route, no expected approach = empty programmed set → backward compat
        AircraftState aircraft = MakeAircraft(route: "");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_ValidationDisabled_AllowsNonProgrammedFix()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO OXNARD");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared, validateDctFixes: false));

        Assert.True(result.Success);
    }

    [Fact]
    public void Dct_MultipleFixes_RejectsIfAnyNonProgrammed()
    {
        AircraftState aircraft = MakeAircraft(route: "SUNOL MODESTO");
        NavigationDatabase navDb = TestNavDbFactory.WithFixes(("SUNOL", 37.5, -121.8), ("RANDOM", 37.0, -121.0));
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);
        var cmd = new DirectToCommand([new ResolvedFix("SUNOL", 37.5, -121.8), new ResolvedFix("RANDOM", 37.0, -121.0)], []);

        CommandResult result = CommandDispatcher.Dispatch(cmd, aircraft, TestDispatch.Context(Random.Shared));

        Assert.False(result.Success);
        Assert.Contains("RANDOM", result.Message);
        Assert.DoesNotContain("SUNOL", result.Message!);
    }
}
