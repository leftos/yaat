using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Vnas;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

public class ProcedureVersionResolutionTests
{
    private static CifpLeg MakeLeg(string fix, CifpPathTerminator pt, CifpAltitudeRestriction? alt)
    {
        return new CifpLeg(fix, pt, null, alt, null, CifpFixRole.None, 0, null, null, null);
    }

    // ── StripTrailingDigits ──

    [Theory]
    [InlineData("BDEGA4", "BDEGA")]
    [InlineData("CNDEL5", "CNDEL")]
    [InlineData("EMZOH4", "EMZOH")]
    [InlineData("BDEGA", "BDEGA")]
    [InlineData("AB", "AB")]
    [InlineData("A1", "A1")] // 2-char minimum preserved
    [InlineData("STAR12", "STAR")]
    public void StripTrailingDigits_ReturnsExpected(string input, string expected)
    {
        Assert.Equal(expected, NavigationDatabase.StripTrailingDigits(input));
    }

    // ── ResolveStarId ──

    [Fact]
    public void ResolveStarId_ExactMatch_ReturnsInput()
    {
        var navDb = NavigationDatabase.ForTesting(
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES", "FAITH"] }
        );

        Assert.Equal("BDEGA4", navDb.ResolveStarId("BDEGA4"));
    }

    [Fact]
    public void ResolveStarId_OutdatedVersion_ReturnsCurrentVersion()
    {
        var navDb = NavigationDatabase.ForTesting(
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES", "FAITH"] }
        );

        Assert.Equal("BDEGA4", navDb.ResolveStarId("BDEGA3"));
    }

    [Fact]
    public void ResolveStarId_NoMatch_ReturnsNull()
    {
        var navDb = NavigationDatabase.ForTesting(
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES", "FAITH"] }
        );

        Assert.Null(navDb.ResolveStarId("XYZZY1"));
    }

    [Fact]
    public void ResolveStarId_NoDigits_ReturnsNull()
    {
        var navDb = NavigationDatabase.ForTesting(
            starBodies: new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES", "FAITH"] }
        );

        Assert.Null(navDb.ResolveStarId("LOZIT"));
    }

    // ── ResolveSidId ──

    [Fact]
    public void ResolveSidId_OutdatedVersion_ReturnsCurrentVersion()
    {
        var navDb = NavigationDatabase.ForTesting(
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL6"] = ["MOLEN", "PORTE", "SUNOL"] }
        );

        Assert.Equal("CNDEL6", navDb.ResolveSidId("CNDEL5"));
    }

    [Fact]
    public void ResolveSidId_ExactMatch_ReturnsInput()
    {
        var navDb = NavigationDatabase.ForTesting(
            sidBodies: new Dictionary<string, IReadOnlyList<string>> { ["CNDEL6"] = ["MOLEN", "PORTE", "SUNOL"] }
        );

        Assert.Equal("CNDEL6", navDb.ResolveSidId("CNDEL6"));
    }

    // ── GetStar CIFP version-agnostic fallback ──

    [Fact]
    public void GetStar_OutdatedVersion_ReturnsCurrent()
    {
        var currentStar = new CifpStarProcedure(
            "KSFO",
            "BDEGA4",
            [],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>()
        );

        var navDb = NavigationDatabase.ForTesting(stars: [currentStar]);

        var result = navDb.GetStar("SFO", "BDEGA3");
        Assert.NotNull(result);
        Assert.Equal("BDEGA4", result.ProcedureId);
    }

    [Fact]
    public void GetSid_OutdatedVersion_ReturnsCurrent()
    {
        var currentSid = new CifpSidProcedure(
            "KSFO",
            "CNDEL6",
            [],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>()
        );

        var navDb = NavigationDatabase.ForTesting(sids: [currentSid]);

        var result = navDb.GetSid("SFO", "CNDEL5");
        Assert.NotNull(result);
        Assert.Equal("CNDEL6", result.ProcedureId);
    }

    // ── ScenarioLoader version resolution ──

    [Fact]
    public void ScenarioLoader_ResolvesOutdatedStar_WithWarning()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["LOZIT"] = (37.80, -122.50),
            ["BDEGA"] = (38.31, -123.06),
            ["CEDES"] = (37.55, -122.30),
            ["FAITH"] = (37.45, -122.35),
            ["BRIXX"] = (37.40, -122.40),
            ["KSFO"] = (37.619, -122.375),
        };

        // NavData has BDEGA4 (current version)
        var starBodies = new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES", "FAITH", "BRIXX"] };

        // CIFP star for altitude profile
        var cifpStar = new CifpStarProcedure(
            "KSFO",
            "BDEGA4",
            [
                MakeLeg("BDEGA", CifpPathTerminator.IF, null),
                MakeLeg("CEDES", CifpPathTerminator.TF, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 12000)),
                MakeLeg("FAITH", CifpPathTerminator.TF, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 8000)),
                MakeLeg("BRIXX", CifpPathTerminator.TF, new CifpAltitudeRestriction(CifpAltitudeRestrictionType.AtOrAbove, 5000)),
            ],
            new Dictionary<string, CifpTransition>(),
            new Dictionary<string, CifpTransition>()
        );

        var navDb = NavigationDatabase.ForTesting(fixes, starBodies: starBodies, stars: [cifpStar]);

        // Scenario references BDEGA3 (outdated)
        var scenarioJson = """
            {
                "id": "test",
                "name": "Test Scenario",
                "aircraft": [{
                    "aircraftId": "AAR142",
                    "aircraftType": "A320",
                    "startingConditions": {
                        "type": "Coordinates",
                        "coordinates": { "lat": 38.5, "lon": -123.5 },
                        "altitude": 18000,
                        "speed": 300,
                        "heading": 150,
                        "navigationPath": "LOZIT BDEGA3"
                    },
                    "onAltitudeProfile": true,
                    "flightplan": {
                        "departure": "KORD",
                        "destination": "SFO",
                        "route": "LOZIT BDEGA3 SFO",
                        "cruiseAltitude": 35000,
                        "cruiseSpeed": 450
                    },
                    "presetCommands": []
                }]
            }
            """;

        var result = ScenarioLoader.Load(scenarioJson, navDb, null, new Random(42));

        // Should have loaded the aircraft
        Assert.Single(result.ImmediateAircraft);
        var aircraft = result.ImmediateAircraft[0];

        // Should have expanded the STAR body (BDEGA4's fixes)
        Assert.True(aircraft.State.Targets.NavigationRoute.Count >= 3, "STAR body should be expanded into navigation route");

        // Should have applied altitude profile (StarViaMode enabled)
        Assert.True(aircraft.State.StarViaMode, "StarViaMode should be enabled via altitude profile");
        Assert.Equal("BDEGA4", aircraft.State.ActiveStarId);

        // Should have generated a warning about the version change
        Assert.Contains(result.Warnings, w => w.Contains("BDEGA3") && w.Contains("BDEGA4"));
    }

    // ── ScenarioValidator procedure issues ──

    [Fact]
    public void Validator_DetectsVersionChange()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)> { ["BDEGA"] = (38.31, -123.06), ["CEDES"] = (37.55, -122.30) };

        var starBodies = new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES"] };

        var navDb = NavigationDatabase.ForTesting(fixes, starBodies: starBodies);

        var scenario = new Scenario
        {
            Id = "test",
            Name = "Test",
            Aircraft =
            [
                new ScenarioAircraft
                {
                    AircraftId = "AAR142",
                    AircraftType = "A320",
                    StartingConditions = new StartingConditions { Type = "Coordinates", NavigationPath = "BDEGA3" },
                    FlightPlan = new ScenarioFlightPlan { Departure = "KORD", Destination = "SFO" },
                },
            ],
        };

        var result = ScenarioValidator.Validate(scenario, navDb);
        Assert.Single(result.ProcedureIssues);
        Assert.Equal(ProcedureIssueKind.VersionChanged, result.ProcedureIssues[0].Kind);
        Assert.Equal("BDEGA3", result.ProcedureIssues[0].ProcedureId);
        Assert.Equal("BDEGA4", result.ProcedureIssues[0].ResolvedId);
    }

    [Fact]
    public void Validator_DetectsNotFound()
    {
        // Fix "XYZZY" exists as a base name but "XYZZY1" has no SID/STAR match
        var fixes = new Dictionary<string, (double Lat, double Lon)> { ["XYZZY"] = (37.0, -122.0) };

        var navDb = NavigationDatabase.ForTesting(fixes);

        var scenario = new Scenario
        {
            Id = "test",
            Name = "Test",
            Aircraft =
            [
                new ScenarioAircraft
                {
                    AircraftId = "UAL123",
                    AircraftType = "B738",
                    StartingConditions = new StartingConditions { Type = "Coordinates", NavigationPath = "XYZZY1" },
                    FlightPlan = new ScenarioFlightPlan { Departure = "KORD", Destination = "KSFO" },
                },
            ],
        };

        var result = ScenarioValidator.Validate(scenario, navDb);
        Assert.Single(result.ProcedureIssues);
        Assert.Equal(ProcedureIssueKind.NotFound, result.ProcedureIssues[0].Kind);
        Assert.Equal("XYZZY1", result.ProcedureIssues[0].ProcedureId);
    }

    [Fact]
    public void Validator_NoProcedureIssues_WhenExactMatch()
    {
        var starBodies = new Dictionary<string, IReadOnlyList<string>> { ["BDEGA4"] = ["BDEGA", "CEDES"] };

        var navDb = NavigationDatabase.ForTesting(starBodies: starBodies);

        var scenario = new Scenario
        {
            Id = "test",
            Name = "Test",
            Aircraft =
            [
                new ScenarioAircraft
                {
                    AircraftId = "AAR142",
                    AircraftType = "A320",
                    StartingConditions = new StartingConditions { Type = "Coordinates", NavigationPath = "BDEGA4" },
                },
            ],
        };

        var result = ScenarioValidator.Validate(scenario, navDb);
        Assert.Empty(result.ProcedureIssues);
    }
}
