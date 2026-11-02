using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Scenarios;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for PopulateNavigationRoute in ScenarioLoader — airway expansion
/// and fix names with trailing digits (airport codes like C83).
/// </summary>
[Collection("NavDbMutator")]
public class NavigationRoutePopulationTests
{
    public NavigationRoutePopulationTests()
    {
        TestVnasData.EnsureInitialized();
    }

    private static ScenarioLoadResult LoadWithNavPath(string navigationPath)
    {
        string scenarioJson = $$"""
            {
                "id": "test",
                "name": "Test",
                "aircraft": [{
                    "aircraftId": "N1234",
                    "aircraftType": "C172",
                    "startingConditions": {
                        "type": "Coordinates",
                        "coordinates": { "lat": 37.0, "lon": -122.0 },
                        "altitude": 5000,
                        "speed": 120,
                        "heading": 360,
                        "navigationPath": "{{navigationPath}}"
                    },
                    "flightplan": {
                        "departure": "KSFO",
                        "destination": "KLAX",
                        "route": "{{navigationPath}}",
                        "cruiseAltitude": 8000,
                        "cruiseSpeed": 120
                    },
                    "presetCommands": []
                }]
            }
            """;

        return ScenarioLoader.Load(scenarioJson, null, new SerializableRandom(42), MagneticDeclination.EvaluationDateUtc);
    }

    private static string[] RouteFixNames(ScenarioLoadResult result) =>
        [.. result.ImmediateAircraft[0].State.Targets.NavigationRoute.Select(t => t.Name)];

    // ── Airway expansion ──

    [Fact]
    public void Airway_ExpandedBetweenAdjacentFixes()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["FIX_A"] = (37.5, -122.0),
            ["FIX_B"] = (37.0, -121.5),
            ["FIX_C"] = (36.5, -121.0),
            ["FIX_D"] = (36.0, -120.5),
        };

        var airways = new Dictionary<string, IReadOnlyList<string>> { ["V108"] = (IReadOnlyList<string>)["FIX_A", "FIX_B", "FIX_C", "FIX_D"] };

        var navDb = NavigationDatabase.ForTesting(fixes, airways: airways);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        ScenarioLoadResult result = LoadWithNavPath("FIX_A V108 FIX_C");

        Assert.Single(result.ImmediateAircraft);
        string[] names = RouteFixNames(result);

        // Should expand V108 segment from FIX_A to FIX_C
        Assert.Contains("FIX_A", names);
        Assert.Contains("FIX_B", names);
        Assert.Contains("FIX_C", names);
        Assert.DoesNotContain("FIX_D", names);
    }

    [Fact]
    public void Airway_NoWarning()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["FIX_A"] = (37.5, -122.0),
            ["FIX_B"] = (37.0, -121.5),
        };

        var airways = new Dictionary<string, IReadOnlyList<string>> { ["V108"] = (IReadOnlyList<string>)["FIX_A", "FIX_B"] };

        var navDb = NavigationDatabase.ForTesting(fixes, airways: airways);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        ScenarioLoadResult result = LoadWithNavPath("FIX_A V108 FIX_B");

        Assert.DoesNotContain(result.Warnings, w => w.Contains("V108"));
    }

    [Fact]
    public void Airway_SkippedWhenNoPreviousFix()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["FIX_B"] = (37.0, -121.5),
        };

        var airways = new Dictionary<string, IReadOnlyList<string>> { ["V108"] = (IReadOnlyList<string>)["FIX_A", "FIX_B"] };

        var navDb = NavigationDatabase.ForTesting(fixes, airways: airways);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // V108 is the first token — no previous fix to expand from
        ScenarioLoadResult result = LoadWithNavPath("V108 FIX_B");

        Assert.Single(result.ImmediateAircraft);
        // Should still resolve FIX_B as a regular fix
        string[] names = RouteFixNames(result);
        Assert.Contains("FIX_B", names);
    }

    [Fact]
    public void Airway_SkippedWhenNoNextFix()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["FIX_A"] = (37.5, -122.0),
        };

        var airways = new Dictionary<string, IReadOnlyList<string>> { ["V108"] = (IReadOnlyList<string>)["FIX_A", "FIX_B"] };

        var navDb = NavigationDatabase.ForTesting(fixes, airways: airways);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        // V108 is the last token — no next fix to expand to
        ScenarioLoadResult result = LoadWithNavPath("FIX_A V108");

        Assert.Single(result.ImmediateAircraft);
        string[] names = RouteFixNames(result);
        Assert.Contains("FIX_A", names);
    }

    // ── Fix names ending in digits (airport codes like C83) ──

    [Fact]
    public void AirportCode_WithTrailingDigits_ResolvedExactly()
    {
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["C83"] = (37.828, -121.763),
        };

        var navDb = NavigationDatabase.ForTesting(fixes);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        ScenarioLoadResult result = LoadWithNavPath("C83");

        Assert.Single(result.ImmediateAircraft);
        string[] names = RouteFixNames(result);
        Assert.Contains("C83", names);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("C83"));
    }

    /// <summary>
    /// A procedure-shaped token that resolves as nothing at all is reported as an unknown STAR whose published speeds
    /// were not applied (7110.65 5-7-1.d NOTE). Airway and airport-code tokens are not, as
    /// <see cref="Airway_NoWarning"/> and <see cref="AirportCode_WithTrailingDigits_ResolvedExactly"/> pin.
    /// </summary>
    [Fact]
    public void MisspeltStar_WarnsThatPublishedSpeedsWereNotApplied()
    {
        ScenarioLoadResult result = LoadWithNavPath("ANJLLL9");

        Assert.Single(result.ImmediateAircraft);
        Assert.Contains(
            result.Warnings,
            w =>
                w.Contains("STAR ANJLLL9 not found in NavData", StringComparison.Ordinal)
                && w.Contains("published speeds not applied", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// A path names things that are no STAR and still resolve — an airway, a fix/radial/distance point, a SID and a
    /// military route: none of them is reported as an unknown STAR whose published speeds were not applied. The negative
    /// cases are <see cref="Airway_NoWarning"/> and <see cref="AirportCode_WithTrailingDigits_ResolvedExactly"/>.
    /// </summary>
    [Fact]
    public void NonStarTokens_AreNotReportedAsUnknownStars()
    {
        const string Airway = "V27";
        const string Frd = "SAT263043";
        const string Sid = "WESLA5";
        const string MilitaryRoute = "IR149";

        NavigationDatabase navDb = NavigationDatabase.Instance;
        Assert.True(navDb.IsAirway(Airway), $"{Airway} is no airway in this navdata");
        Assert.NotNull(navDb.ResolveFixOrFrd(Frd));
        Assert.Equal(Sid, navDb.ResolveSidId(Sid));
        Assert.True(navDb.IsMilitaryRoute(MilitaryRoute), $"{MilitaryRoute} is no military route in this navdata");

        ScenarioLoadResult result = LoadWithNavPath($"{Airway} {Frd} {Sid} {MilitaryRoute}");

        Assert.Single(result.ImmediateAircraft);
        Assert.DoesNotContain(result.Warnings, w => w.Contains("not found in NavData", StringComparison.Ordinal));
    }

    [Fact]
    public void FixName_AllDigitSuffix_TriesExactFirst()
    {
        // Fix "Q136" exists — should not be stripped to "Q"
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["Q136"] = (37.5, -122.0),
        };

        var navDb = NavigationDatabase.ForTesting(fixes);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        ScenarioLoadResult result = LoadWithNavPath("Q136");

        Assert.Single(result.ImmediateAircraft);
        string[] names = RouteFixNames(result);
        Assert.Contains("Q136", names);
    }

    [Fact]
    public void FixName_UnknownToken_NotStripped_WarningEmitted()
    {
        // "BDEGA4" is not a SID/STAR/fix — emitted as-is, unresolvable → warning
        var fixes = new Dictionary<string, (double Lat, double Lon)>
        {
            ["KSFO"] = (37.619, -122.375),
            ["KLAX"] = (33.943, -118.408),
            ["BDEGA"] = (38.31, -123.06),
        };

        var navDb = NavigationDatabase.ForTesting(fixes);
        using IDisposable _ = NavigationDatabase.ScopedOverride(navDb);

        ScenarioLoadResult result = LoadWithNavPath("BDEGA4");

        Assert.Single(result.ImmediateAircraft);
        string[] names = RouteFixNames(result);
        Assert.DoesNotContain("BDEGA", names);
        Assert.Contains(result.Warnings, w => w.Contains("BDEGA4"));
    }
}
