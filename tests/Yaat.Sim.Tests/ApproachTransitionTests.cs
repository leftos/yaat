using System.Linq;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Phases.Approach;

namespace Yaat.Sim.Tests;

public class ApproachTransitionTests
{
    private static AircraftState MakeAircraft(
        string route = "",
        double heading = 280,
        double lat = 37.75,
        double lon = -122.35,
        string destination = "OAK"
    )
    {
        return new AircraftState
        {
            Callsign = "N123",
            AircraftType = "B738",
            Heading = heading,
            Altitude = 5000,
            Latitude = lat,
            Longitude = lon,
            Destination = destination,
            Route = route,
        };
    }

    private static CifpApproachProcedure MakeProcedureWithTransitions()
    {
        var transitions = new Dictionary<string, CifpTransition>
        {
            ["FFIST"] = new CifpTransition(
                "FFIST",
                [
                    new CifpLeg("FFIST", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                    new CifpLeg("GROVE", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                ]
            ),
            ["CNDEL"] = new CifpTransition(
                "CNDEL",
                [
                    new CifpLeg("CNDEL", CifpPathTerminator.IF, null, null, null, CifpFixRole.IAF, 10, null, null, null),
                    new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 20, null, null, null),
                ]
            ),
        };

        return new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 30, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 40, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 50, null, null, null),
                new CifpLeg("RW28R", CifpPathTerminator.TF, null, null, null, CifpFixRole.MAHP, 60, null, null, null),
            ],
            transitions,
            [],
            false,
            null
        );
    }

    private static CifpApproachProcedure MakeProcedureWithoutTransitions()
    {
        return new CifpApproachProcedure(
            "OAK",
            "I28R",
            'I',
            "ILS",
            "28R",
            [
                new CifpLeg("GROVE", CifpPathTerminator.TF, null, null, null, CifpFixRole.IAF, 30, null, null, null),
                new CifpLeg("FITKI", CifpPathTerminator.TF, null, null, null, CifpFixRole.IF, 40, null, null, null),
                new CifpLeg("BERYL", CifpPathTerminator.TF, null, null, null, CifpFixRole.FAF, 50, null, null, null),
                new CifpLeg("RW28R", CifpPathTerminator.TF, null, null, null, CifpFixRole.MAHP, 60, null, null, null),
            ],
            new Dictionary<string, CifpTransition>(),
            [],
            false,
            null
        );
    }

    private static NavigationDatabase MakeNavDb()
    {
        return NavigationDatabase.ForTesting(
            new Dictionary<string, (double Lat, double Lon)>
            {
                ["FFIST"] = (37.80, -122.40),
                ["CNDEL"] = (37.85, -122.50),
                ["GROVE"] = (37.78, -122.35),
                ["FITKI"] = (37.76, -122.30),
                ["BERYL"] = (37.74, -122.25),
                ["RW28R"] = (37.72, -122.22),
            }
        );
    }

    // --- SelectBestTransition ---

    [Fact]
    public void SelectBestTransition_NoTransitions_ReturnsNull()
    {
        var procedure = MakeProcedureWithoutTransitions();
        var aircraft = MakeAircraft();

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, null);

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestTransition_RouteMatchesTransitionFix_ReturnsThatTransition()
    {
        var procedure = MakeProcedureWithTransitions();
        var aircraft = MakeAircraft(route: "SUNOL FFIST OAK");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, MakeNavDb());

        Assert.NotNull(result);
        Assert.Equal("FFIST", result.Name);
    }

    [Fact]
    public void SelectBestTransition_RouteMatchesSecondTransition_ReturnsThatTransition()
    {
        var procedure = MakeProcedureWithTransitions();
        var aircraft = MakeAircraft(route: "SUNOL CNDEL OAK");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, MakeNavDb());

        Assert.NotNull(result);
        Assert.Equal("CNDEL", result.Name);
    }

    [Fact]
    public void SelectBestTransition_NoRouteMatch_FallsBackToNearestAhead()
    {
        var procedure = MakeProcedureWithTransitions();
        // Aircraft heading east (90°) at position where FFIST is roughly ahead
        var aircraft = MakeAircraft(route: "", heading: 350, lat: 37.75, lon: -122.40);
        var navDb = MakeNavDb();

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, navDb);

        // Should pick the nearest IAF that's within ±90° of heading
        Assert.NotNull(result);
    }

    [Fact]
    public void SelectBestTransition_NoNavDb_NoRouteMatch_ReturnsNull()
    {
        var procedure = MakeProcedureWithTransitions();
        var aircraft = MakeAircraft(route: "");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, null);

        Assert.Null(result);
    }

    [Fact]
    public void SelectBestTransition_DotAirwayRoute_ParsesFixName()
    {
        var procedure = MakeProcedureWithTransitions();
        var aircraft = MakeAircraft(route: "SUNOL.V25 FFIST.V244 OAK");

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, MakeNavDb());

        Assert.NotNull(result);
        Assert.Equal("FFIST", result.Name);
    }

    [Fact]
    public void SelectBestTransition_NavRouteMatchesTransition_ReturnsThatTransition()
    {
        var procedure = MakeProcedureWithTransitions();
        var aircraft = MakeAircraft(route: "");
        aircraft.Targets.NavigationRoute.Add(
            new NavigationTarget
            {
                Name = "CNDEL",
                Latitude = 37.85,
                Longitude = -122.50,
            }
        );

        var result = ApproachCommandHandler.SelectBestTransition(procedure, aircraft, MakeNavDb());

        Assert.NotNull(result);
        Assert.Equal("CNDEL", result.Name);
    }

    // --- GetApproachFixNames with transitions ---

    [Fact]
    public void GetApproachFixNames_WithTransitions_IncludesTransitionAndCommonFixes()
    {
        var procedure = MakeProcedureWithTransitions();

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        Assert.Contains("FFIST", names);
        Assert.Contains("CNDEL", names);
        Assert.Contains("GROVE", names);
        Assert.Contains("FITKI", names);
        Assert.Contains("BERYL", names);
        Assert.DoesNotContain("RW28R", names); // MAHP excluded
    }

    [Fact]
    public void GetApproachFixNames_WithoutTransitions_ReturnsCommonOnly()
    {
        var procedure = MakeProcedureWithoutTransitions();

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        Assert.Contains("GROVE", names);
        Assert.Contains("FITKI", names);
        Assert.Contains("BERYL", names);
        Assert.DoesNotContain("RW28R", names);
    }

    [Fact]
    public void GetApproachFixNames_DeduplicatesBoundaryFix()
    {
        var procedure = MakeProcedureWithTransitions();

        var names = ApproachCommandHandler.GetApproachFixNames(procedure);

        // GROVE appears in both FFIST transition and common legs; should only appear once
        int groveCount = names.Count(n => n.Equals("GROVE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, groveCount);
    }

    // --- CAPP with transition ---

    [Fact]
    public void Capp_WithTransition_BuildsFullFixSequence()
    {
        var procedure = MakeProcedureWithTransitions();
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );
        var navDb = TestNavDbFactory.WithFixesRunwayAndApproaches(
            [
                ("FFIST", 37.80, -122.40),
                ("GROVE", 37.78, -122.35),
                ("FITKI", 37.76, -122.30),
                ("BERYL", 37.74, -122.25),
                ("RW28R", 37.72, -122.22),
                ("CNDEL", 37.85, -122.50),
            ],
            runway,
            [procedure]
        );

        // Aircraft with FFIST in route → should select FFIST transition
        var aircraft = MakeAircraft(route: "SUNOL FFIST OAK");
        var cmd = new ClearedApproachCommand("I28R", "OAK", false, null, null, null, null, null, null, null, null);

        var result = ApproachCommandHandler.TryClearedApproach(cmd, aircraft, navDb);

        Assert.True(result.Success, result.Message);
        Assert.NotNull(aircraft.Phases);

        // The approach nav phase should contain transition + common fixes
        var navPhase = aircraft.Phases.Phases.OfType<ApproachNavigationPhase>().FirstOrDefault();
        Assert.NotNull(navPhase);

        var fixNames = navPhase.Fixes.Select(f => f.Name).ToList();
        Assert.Contains("FFIST", fixNames);
        Assert.Contains("BERYL", fixNames);
        // GROVE should not be duplicated (boundary fix dedup)
        int groveCount = fixNames.Count(n => n.Equals("GROVE", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, groveCount);
    }

    // --- ProgrammedFixResolver with STAR runway transitions ---

    [Fact]
    public void ProgrammedFixResolver_WithStarAndRunway_IncludesRunwayTransitionFixes()
    {
        var star = new CifpStarProcedure(
            "OAK",
            "OAKES3",
            [new CifpLeg("OAKES", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 10, null, null, null)],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>
            {
                ["RW28R"] = new CifpTransition(
                    "RW28R",
                    [
                        new CifpLeg("ARCHI", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null),
                        new CifpLeg("FFIST", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 20, null, null, null),
                    ]
                ),
            }
        );

        var navDb = NavigationDatabase.ForTesting(stars: [star]);

        var result = ProgrammedFixResolver.Resolve("OAKES3", null, "OAK", null, navDb, null, navDb, "OAKES3", "28R");

        Assert.Contains("ARCHI", result);
        Assert.Contains("FFIST", result);
    }

    [Fact]
    public void ProgrammedFixResolver_WithStarNoRunway_DoesNotExpandRunwayTransition()
    {
        var star = new CifpStarProcedure(
            "OAK",
            "OAKES3",
            [new CifpLeg("OAKES", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 10, null, null, null)],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>
            {
                ["RW28R"] = new CifpTransition(
                    "RW28R",
                    [new CifpLeg("ARCHI", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null)]
                ),
            }
        );

        var navDb = NavigationDatabase.ForTesting(stars: [star]);

        var result = ProgrammedFixResolver.Resolve("OAKES3", null, "OAK", null, navDb, null, navDb, "OAKES3", null);

        Assert.DoesNotContain("ARCHI", result);
    }

    [Fact]
    public void ProgrammedFixResolver_DeriveRunwayFromExpectedApproach()
    {
        var procedure = MakeProcedureWithoutTransitions();
        var runway = TestRunwayFactory.Make(
            designator: "28R",
            airportId: "OAK",
            thresholdLat: 37.72,
            thresholdLon: -122.22,
            heading: 280,
            elevationFt: 9
        );

        var star = new CifpStarProcedure(
            "OAK",
            "OAKES3",
            [new CifpLeg("OAKES", CifpPathTerminator.IF, null, null, null, CifpFixRole.None, 10, null, null, null)],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>
            {
                ["RW28R"] = new CifpTransition(
                    "RW28R",
                    [new CifpLeg("ARCHI", CifpPathTerminator.TF, null, null, null, CifpFixRole.None, 10, null, null, null)]
                ),
            }
        );

        var approachesByAirport = new Dictionary<string, IReadOnlyList<CifpApproachProcedure>>(StringComparer.OrdinalIgnoreCase)
        {
            ["OAK"] = [procedure],
        };
        var navDb = NavigationDatabase.ForTesting(null, [runway], approachesByAirport, null, null, [star]);

        // No explicit destinationRunway, but expectedApproach "I28R" → runway "28R"
        var result = ProgrammedFixResolver.Resolve(null, "I28R", "OAK", null, navDb, null, navDb, "OAKES3", null);

        Assert.Contains("ARCHI", result);
    }
}
