using Xunit;
using Yaat.Sim.Data;
using Yaat.Sim.Data.Airport;
using Yaat.Sim.Phases;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AirportSidecarLoader"/> — parses the unified per-airport sidecar JSON files
/// under <c>ARTCCs/{ARTCC}/Airports/*.json</c> into <see cref="AirportSidecar"/> records. Warn-don't-throw:
/// malformed input adds a warning and skips the offending file or section.
/// </summary>
public class AirportSidecarLoaderTests
{
    [Fact]
    public void LoadAll_MissingDirectory_ReturnsWarningNoThrow()
    {
        AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(
            Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid())
        );

        Assert.Empty(result.Airports);
        Assert.Single(result.Warnings);
        Assert.Contains("not found", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAll_BundledTestData_LoadsKoakRoutes()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(baseDir);

        AirportSidecar oak = Assert.Single(result.Airports, a => a.AirportId == "KOAK");
        // Three routes in the bundled oak.json — all load (graph validation happens later, at menu-build time).
        Assert.Equal(3, oak.TaxiRoutes.Count);
        Assert.Empty(result.Warnings);

        TaxiRouteDefinition? dep30 = oak.TaxiRoutes.FirstOrDefault(r => r.Name == "DEP 30 via W");
        Assert.NotNull(dep30);
        Assert.Equal("KOAK", dep30!.AirportId);
        Assert.Equal("W", dep30.Path);
        Assert.Equal("30", dep30.DestinationRunway);

        TaxiRouteDefinition? dep28L = oak.TaxiRoutes.FirstOrDefault(r => r.Name == "DEP 28L via K-W");
        Assert.NotNull(dep28L);
        Assert.Equal(["K", "W"], dep28L!.GetPathTokens());
    }

    [Fact]
    public void LoadAll_ReadsAllSectionsForAirport()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "oak.json"),
                """
                {
                  "airportId": "KOAK",
                  "avoidTaxiways": [ { "name": "S", "notes": "ramp lead" }, { "name": "z" } ],
                  "taxiRoutes": [ { "name": "R1", "path": "T U W", "destinationRunway": "30" } ]
                }
                """
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Empty(result.Warnings);
            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Equal("KOAK", airport.AirportId);
            // avoidTaxiways names are upper-cased and trimmed at load.
            Assert.Equal(["S", "Z"], [.. airport.AvoidTaxiways.Select(t => t.Name)]);
            Assert.Equal("ramp lead", airport.AvoidTaxiways[0].Notes);
            TaxiRouteDefinition route = Assert.Single(airport.TaxiRoutes);
            Assert.Equal("KOAK", route.AirportId);
            Assert.Equal("30", route.DestinationRunway);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_OnlyRoutesNoAvoid_StillLoadsAirport()
    {
        // An airport with only one section (no avoidTaxiways) must still produce a sidecar — unlike the
        // old AvoidTaxiwayLoader which skipped a whole file with zero avoid entries.
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "fll.json"), """{ "airportId": "KFLL", "taxiRoutes": [ { "name": "R", "path": "T B" } ] }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Equal("KFLL", airport.AirportId);
            Assert.Empty(airport.AvoidTaxiways);
            Assert.Single(airport.TaxiRoutes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_WarnsOnMissingAirportId()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "bad.json"), """{ "avoidTaxiways": [ { "name": "S" } ] }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
            Assert.Contains(result.Warnings, w => w.Contains("missing airportId"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_DedupesAvoidNamesWithinFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "dup.json"),
                """{ "airportId": "KOAK", "avoidTaxiways": [ { "name": "S" }, { "name": "s" } ] }"""
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Equal(["S"], [.. airport.AvoidTaxiways.Select(t => t.Name)]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// The <c>movementAreaTaxiways</c> / <c>nonMovementTaxilanes</c> lists load trimmed, upper-cased and de-duplicated,
    /// and a blank name is warned and skipped.
    /// </summary>
    [Fact]
    public void LoadAll_PavementClassLists_TrimUpperCaseDedupeAndWarnOnBlank()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "sfo.json"),
                """
                {
                  "airportId": "KSFO",
                  "movementAreaTaxiways": [ { "name": " b1 " }, { "name": "B1" }, { "name": "t5a", "notes": "diagram" } ],
                  "nonMovementTaxilanes": [ { "name": "m4" }, { "name": "  " } ]
                }
                """
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Equal(["B1", "T5A"], airport.MovementAreaTaxiways);
            Assert.Equal(["M4"], airport.NonMovementTaxilanes);
            string warning = Assert.Single(result.Warnings);
            Assert.Contains("nonMovementTaxilanes[1] missing name", warning, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// The <c>asdexLandingTaxiways</c> list loads trimmed, upper-cased and de-duplicated, and a blank name is warned and
    /// skipped.
    /// </summary>
    [Fact]
    public void LoadAll_AsdexLandingTaxiways_TrimUpperCaseDedupeAndWarnOnBlank()
    {
        AirportSidecarLoadResult result = LoadOakSidecar("""{ "airportId": "KOAK", "asdexLandingTaxiways": [ " a ", "B", "A", "  " ] }""");

        AirportSidecar oak = Assert.Single(result.Airports);
        Assert.NotNull(oak.AsdexLandingTaxiways);
        Assert.Equal(["A", "B"], [.. oak.AsdexLandingTaxiways.OrderBy(name => name, StringComparer.Ordinal)]);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("asdexLandingTaxiways[3] missing name", warning, StringComparison.Ordinal);
    }

    /// <summary>
    /// An absent <c>asdexLandingTaxiways</c> reads as null, which is any taxiway; an empty list reads as an empty set, which
    /// is no taxiway-landing alert.
    /// </summary>
    [Fact]
    public void LoadAll_AsdexLandingTaxiways_AbsentIsNull_EmptyListStaysEmpty()
    {
        AirportSidecar absent = Assert.Single(LoadOakSidecar("""{ "airportId": "KOAK" }""").Airports);
        Assert.Null(absent.AsdexLandingTaxiways);

        AirportSidecar empty = Assert.Single(LoadOakSidecar("""{ "airportId": "KOAK", "asdexLandingTaxiways": [] }""").Airports);
        Assert.NotNull(empty.AsdexLandingTaxiways);
        Assert.Empty(empty.AsdexLandingTaxiways);
    }

    /// <summary>
    /// A <c>crossingPairs</c> entry loads trimmed and upper-cased only when it names two distinct taxiways and a runway with both
    /// ends; one with a wrong taxiway count, a duplicated name, no runway or a one-ended runway is warned and skipped.
    /// </summary>
    [Fact]
    public void LoadAll_CrossingPairs_RequireTwoDistinctTaxiwaysAndATwoEndedRunway()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "cos.json"),
                """
                {
                  "airportId": "KCOS",
                  "crossingPairs": [
                    { "taxiways": [" a2 ", "c2"], "runway": "17r/35l", "notes": "declared" },
                    { "taxiways": ["A2"], "runway": "17R/35L" },
                    { "taxiways": ["A2", "a2"], "runway": "17R/35L" },
                    { "taxiways": ["A3", "C3"] },
                    { "taxiways": ["A4", "C4"], "runway": "17R" },
                    { "taxiways": ["A5", "C5"], "runway": "17R/35L/35R" }
                  ]
                }
                """
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            CrossingPair pair = Assert.Single(airport.CrossingPairs);
            Assert.Equal(("A2", "C2", "17R/35L"), (pair.FirstTaxiway, pair.SecondTaxiway, pair.Runway));
            Assert.Collection(
                result.Warnings,
                w => Assert.Contains("crossingPairs[1] requires exactly 2 distinct non-blank taxiways", w, StringComparison.Ordinal),
                w => Assert.Contains("crossingPairs[2] requires exactly 2 distinct non-blank taxiways", w, StringComparison.Ordinal),
                w => Assert.Contains("crossingPairs[3] (A3/C3) missing runway", w, StringComparison.Ordinal),
                w => Assert.Contains("crossingPairs[4] (A4/C4) runway '17R' must name both ends", w, StringComparison.Ordinal),
                w => Assert.Contains("crossingPairs[5] (A5/C5) runway '17R/35L/35R' must name both ends", w, StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_SkipsRouteWithConflictingDestinations_WithWarning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "kxxx.json"),
                """
                {
                  "airportId": "KXXX",
                  "taxiRoutes": [
                    { "name": "ambiguous", "path": "A", "destinationRunway": "10R", "destinationParking": "G7" }
                  ]
                }
                """
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Empty(airport.TaxiRoutes);
            Assert.Contains(result.Warnings, w => w.Contains("destination", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_MalformedJson_AddsWarningAndContinues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "broken.json"), "{ this is not valid json");
            File.WriteAllText(
                Path.Combine(categoryDir, "good.json"),
                """{ "airportId": "KSFO", "taxiRoutes": [ { "name": "test", "path": "A" } ] }"""
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            Assert.Equal("KSFO", airport.AirportId);
            Assert.Contains(result.Warnings, w => w.Contains("broken.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_DiscoversAirportsAcrossMultipleArtccs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string zabDir = Path.Combine(tempDir, "ZAB", "Airports");
        string zlaDir = Path.Combine(tempDir, "ZLA", "Airports");
        Directory.CreateDirectory(zabDir);
        Directory.CreateDirectory(zlaDir);
        try
        {
            File.WriteAllText(Path.Combine(zabDir, "abq.json"), """{ "airportId": "KABQ", "taxiRoutes": [{ "name": "ABQ test", "path": "A" }] }""");
            File.WriteAllText(Path.Combine(zlaDir, "lax.json"), """{ "airportId": "KLAX", "avoidTaxiways": [{ "name": "Z" }] }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Equal(2, result.Airports.Count);
            Assert.Contains(result.Airports, a => a.AirportId == "KABQ");
            Assert.Contains(result.Airports, a => a.AirportId == "KLAX");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_IgnoresFilesOutsideAirportsSubfolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        // A sibling category folder must not be scanned by the airport-sidecar loader.
        string otherDir = Path.Combine(tempDir, "ZTEST", "CustomFixes");
        Directory.CreateDirectory(otherDir);
        try
        {
            File.WriteAllText(Path.Combine(otherDir, "oak.json"), """{ "airportId": "KOAK", "avoidTaxiways": [ { "name": "S" } ] }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_ParsesOneWayEdges_AndValidates()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "sfo.json"),
                """
                {
                  "airportId": "KSFO",
                  "oneWayEdges": [
                    { "block": "both", "path": [ { "point": [-122.39, 37.61], "taxiway": "a" }, { "point": [-122.38, 37.62] } ] },
                    { "path": [ { "point": [-122.39, 37.61] } ] },
                    { "path": [ { "point": [1, 2, 3], "taxiway": "X" }, { "point": [4, 5] } ] }
                  ]
                }
                """
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar airport = Assert.Single(result.Airports);
            OneWayConstraint constraint = Assert.Single(airport.OneWayEdges);
            Assert.True(constraint.BlockBoth);
            Assert.Equal(2, constraint.Path.Count);
            // point is [lon, lat] → Lat=37.61, Lon=-122.39; taxiway upper-cased.
            Assert.Equal(37.61, constraint.Path[0].Lat);
            Assert.Equal(-122.39, constraint.Path[0].Lon);
            Assert.Equal("A", constraint.Path[0].Taxiway);
            Assert.Null(constraint.Path[1].Taxiway);
            // The single-point path and the malformed-coordinate path are both skipped with warnings.
            Assert.Equal(2, result.Warnings.Count);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_OneWayExemptWakeClassesNull_ReadsAsNoExemption()
    {
        AirportSidecarLoadResult result = LoadOneWaySidecar(
            """{ "exemptWakeClasses": null, "path": [ { "point": [-122.39, 37.61] }, { "point": [-122.38, 37.62] } ] }"""
        );

        OneWayConstraint constraint = Assert.Single(Assert.Single(result.Airports).OneWayEdges);
        Assert.Empty(constraint.ExemptWakeClasses);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void LoadAll_OneWayExemptingEveryWakeClass_Warns()
    {
        AirportSidecarLoadResult result = LoadOneWaySidecar(
            """{ "exemptWakeClasses": ["Small", "Large", "Heavy", "Super"], "path": [ { "point": [-122.39, 37.61] }, { "point": [-122.38, 37.62] } ] }"""
        );

        OneWayConstraint constraint = Assert.Single(Assert.Single(result.Airports).OneWayEdges);
        Assert.Equal(4, constraint.ExemptWakeClasses.Count);
        string warning = Assert.Single(result.Warnings);
        Assert.Contains("oneWayEdges[0]", warning);
        Assert.Contains("exempts every wake class", warning);
    }

    /// <summary>
    /// A <c>standDeparture</c> entry with a blank stand name, and one with a value no stand departure names, each warn and
    /// are skipped.
    /// </summary>
    [Fact]
    public void LoadAll_StandDepartureBlankNameAndBadValue_WarnAndSkip()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "oak.json"),
                """{ "airportId": "KOAK", "standDeparture": { " ": "TaxiOut", "GA3": "Sideways", "GA4": " taxiout " } }"""
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            AirportSidecar oak = Assert.Single(result.Airports);
            Assert.Equal(StandDeparture.TaxiOut, Assert.Single(oak.StandDepartureOverrides, kv => kv.Key == "GA4").Value);
            Assert.Single(oak.StandDepartureOverrides);
            Assert.Equal(2, result.Warnings.Count);
            Assert.Contains(result.Warnings, w => w.Contains("blank stand name", StringComparison.Ordinal));
            Assert.Contains(
                result.Warnings,
                w => w.Contains("standDeparture[GA3]", StringComparison.Ordinal) && w.Contains("'Sideways'", StringComparison.Ordinal)
            );
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>A <c>standDeparture</c> entry naming <c>Either</c> loads with no warning.</summary>
    [Fact]
    public void LoadAll_StandDepartureEither_Loads()
    {
        AirportSidecarLoadResult result = LoadOakSidecar("""{ "airportId": "KOAK", "standDeparture": { "MTN1": "either" } }""");

        AirportSidecar oak = Assert.Single(result.Airports);
        Assert.Equal(StandDeparture.Either, Assert.Single(oak.StandDepartureOverrides, kv => kv.Key == "MTN1").Value);
        Assert.Empty(result.Warnings);
    }

    /// <summary>
    /// The <c>standDepartureAreas</c> rules load in file order with the runway end zero-pad-normalized; a rule missing its
    /// runway, with a side other than left/right, or with a departure no <see cref="StandDeparture"/> names, warns and is
    /// skipped.
    /// </summary>
    [Fact]
    public void LoadAll_StandDepartureAreas_ParsesAndSkipsBadEntries()
    {
        AirportSidecarLoadResult result = LoadOakSidecar(
            """
            {
              "airportId": "KOAK",
              "standDepartureAreas": [
                { "side": "left", "departure": "TaxiOut" },
                { "runway": "28R", "side": "up", "departure": "TaxiOut" },
                { "runway": "28R", "side": "right", "departure": "Sideways" },
                { "runway": "28r", "side": " Right ", "departure": "either", "notes": "North Field" },
                { "runway": "9", "side": "left", "departure": "PushBack" }
              ]
            }
            """
        );

        AirportSidecar oak = Assert.Single(result.Airports);
        Assert.Equal(
            [
                new StandDepartureArea("28R", ExitSide.Right, StandDeparture.Either, "North Field"),
                new StandDepartureArea("09", ExitSide.Left, StandDeparture.PushBack, null),
            ],
            oak.StandDepartureAreas
        );
        Assert.Equal(3, result.Warnings.Count);
        Assert.Contains(result.Warnings, w => w.Contains("standDepartureAreas[0] missing runway", StringComparison.Ordinal));
        Assert.Contains(
            result.Warnings,
            w => w.Contains("standDepartureAreas[1]", StringComparison.Ordinal) && w.Contains("'up'", StringComparison.Ordinal)
        );
        Assert.Contains(
            result.Warnings,
            w =>
                w.Contains("standDepartureAreas[2]", StringComparison.Ordinal)
                && w.Contains("'Sideways'", StringComparison.Ordinal)
                && w.Contains("'Either'", StringComparison.Ordinal)
        );
    }

    /// <summary>
    /// Two sidecars for one airport in one ARTCC folder: the file later in ordinal path order (<c>a.json</c> after
    /// <c>B.json</c>) wins a per-name clash, so the loaded result does not depend on the order the OS lists them.
    /// </summary>
    [Fact]
    public void LoadAll_PerNameOverride_LastFileInOrdinalPathOrderWins()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "B.json"), """{ "airportId": "KOAK", "standDeparture": { "GA1": "PushBack" } }""");
            File.WriteAllText(Path.Combine(categoryDir, "a.json"), """{ "airportId": "KOAK", "standDeparture": { "GA1": "TaxiOut" } }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            var catalog = new AirportSidecarCatalog(result.Airports);
            Assert.Equal(StandDeparture.TaxiOut, catalog.GetStandDepartureOverrides("KOAK")["GA1"]);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// Two sidecars for one airport disagreeing over stand-departure areas: rules concatenate in load order, so the rule
    /// from the file earlier in ordinal path order (<c>B.json</c> before <c>a.json</c>) comes first and matches first.
    /// </summary>
    [Fact]
    public void LoadAll_StandDepartureAreas_FirstMatchInOrdinalPathOrderWins()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "B.json"),
                """{ "airportId": "KOAK", "standDepartureAreas": [ { "runway": "28R", "side": "right", "departure": "TaxiOut", "notes": "B" } ] }"""
            );
            File.WriteAllText(
                Path.Combine(categoryDir, "a.json"),
                """{ "airportId": "KOAK", "standDepartureAreas": [ { "runway": "28R", "side": "right", "departure": "PushBack", "notes": "a" } ] }"""
            );

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            var catalog = new AirportSidecarCatalog(result.Airports);
            IReadOnlyList<StandDepartureArea> areas = catalog.GetStandDepartureAreas("KOAK");
            Assert.Equal(2, areas.Count);
            Assert.Equal("B", areas[0].Notes);
            Assert.Equal("a", areas[1].Notes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// The whole tree orders by each file's path relative to the ARTCCs base, ordinal, so <c>B/…</c> before <c>a/…</c>
    /// and the later file's per-name value wins — the ARTCC folder's own listing order is irrelevant.
    /// </summary>
    [Fact]
    public void LoadAll_ArtccFolders_OrderByOrdinalRelativePath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string upperDir = Path.Combine(tempDir, "B", "Airports");
        string lowerDir = Path.Combine(tempDir, "a", "Airports");
        Directory.CreateDirectory(upperDir);
        Directory.CreateDirectory(lowerDir);
        try
        {
            File.WriteAllText(Path.Combine(upperDir, "oak.json"), """{ "airportId": "KOAK", "standDeparture": { "GA1": "PushBack" } }""");
            File.WriteAllText(Path.Combine(lowerDir, "oak.json"), """{ "airportId": "KOAK", "standDeparture": { "GA1": "TaxiOut" } }""");

            AirportSidecarLoadResult result = AirportSidecarLoader.LoadAll(tempDir);

            var catalog = new AirportSidecarCatalog(result.Airports);
            Assert.Equal(StandDeparture.TaxiOut, catalog.GetStandDepartureOverrides("KOAK")["GA1"]);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>
    /// The sidecar files are the whole tree's <c>Airports/*.json</c> in the order the hash keys them: the ordinal order of
    /// each path relative to the ARTCCs base, with forward slashes, so <c>A-B/…</c> precedes <c>A/…</c>.
    /// </summary>
    [Fact]
    public void SidecarFilesInLoadOrder_MatchesTheHashOrder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string plainDir = Path.Combine(tempDir, "A", "Airports");
        string dashedDir = Path.Combine(tempDir, "A-B", "Airports");
        Directory.CreateDirectory(plainDir);
        Directory.CreateDirectory(dashedDir);
        try
        {
            File.WriteAllText(Path.Combine(plainDir, "oak.json"), """{ "airportId": "KOAK" }""");
            File.WriteAllText(Path.Combine(dashedDir, "oak.json"), """{ "airportId": "KOAK" }""");

            IReadOnlyList<string> files = AirportSidecarLoader.SidecarFilesInLoadOrder(tempDir);

            // Ordinal '-' (0x2D) sorts before '/', so A-B/Airports/... precedes A/Airports/... even when the OS lists
            // A first — the same key the sidecar hash orders the airport's files by.
            Assert.Equal(
                ["A-B/Airports/oak.json", "A/Airports/oak.json"],
                [.. files.Select(file => Path.GetRelativePath(tempDir, file).Replace('\\', '/'))]
            );
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>A valid sibling entry the spot-edge warning cases load beside the bad one.</summary>
    private const string ValidSpotEdge = """{ "name": "RAMP", "path": [ { "spot": "1" }, { "spot": "2" } ] }""";

    /// <summary>
    /// A chain of all four point kinds loads normalised: the name, junction names and taxiway trimmed and upper-cased, the
    /// spot's leading <c>$</c> stripped, and each <c>[lon, lat]</c> read as <c>LatLon(lat, lon)</c>.
    /// </summary>
    [Fact]
    public void SpotEdges_FourKindChain_ParsesNormalised()
    {
        (AirportSidecarLoadResult result, _) = LoadSpotEdges(
            """
            {
              "name": " n8 ",
              "notes": "charted lane",
              "path": [
                { "spot": " $22 " },
                { "junction": [" n ", "n7"] },
                { "taxiway": " p ", "near": [-80.275657, 25.789645] },
                { "point": [-80.2751, 25.7902] }
              ]
            }
            """
        );

        Assert.Empty(result.Warnings);
        SpotEdgeChain chain = Assert.Single(Assert.Single(result.Airports).SpotEdges);
        Assert.Equal("N8", chain.Name);
        Assert.Equal("charted lane", chain.Notes);
        Assert.Equal(
            [
                new SpotEdgePoint(SpotEdgePointKind.Spot, "22", null, null, null, null),
                new SpotEdgePoint(SpotEdgePointKind.Junction, null, "N", "N7", null, null),
                new SpotEdgePoint(SpotEdgePointKind.TaxiwayNear, null, null, null, "P", new LatLon(25.789645, -80.275657)),
                new SpotEdgePoint(SpotEdgePointKind.Point, null, null, null, null, new LatLon(25.7902, -80.2751)),
            ],
            chain.Path
        );
    }

    /// <summary>
    /// Each authored problem warns with its exact text and skips the whole entry, while a valid sibling entry in the same
    /// file still loads.
    /// </summary>
    [Theory]
    [InlineData("""{ "path": [ { "spot": "1" }, { "spot": "2" } ] }""", "?", "no name")]
    [InlineData("""{ "name": " ", "path": [ { "spot": "1" }, { "spot": "2" } ] }""", "?", "no name")]
    [InlineData("""{ "name": "a", "path": [ { "spot": "1" } ] }""", "a", "fewer than two points")]
    [InlineData("""{ "name": "a" }""", "a", "fewer than two points")]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1" }, { } ] }""", "A", "point 1 has no resolver")]
    [InlineData("""{ "name": "A", "path": [ { "near": [-80.1, 25.1] }, { "spot": "2" } ] }""", "A", "point 0 has no resolver")]
    [InlineData(
        """{ "name": "A", "path": [ { "spot": "1", "point": [-80.1, 25.1] }, { "spot": "2" } ] }""",
        "A",
        "point 0 has more than one resolver"
    )]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1", "near": [-80.1, 25.1] }, { "spot": "2" } ] }""", "A", "point 0 has near without taxiway")]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1" }, { "taxiway": "N" } ] }""", "A", "point 1 has taxiway without near")]
    [InlineData("""{ "name": "A", "path": [ { "junction": ["N"] }, { "spot": "2" } ] }""", "A", "point 0 junction needs two taxiway names")]
    [InlineData("""{ "name": "A", "path": [ { "junction": ["N", " "] }, { "spot": "2" } ] }""", "A", "point 0 junction needs two taxiway names")]
    [InlineData("""{ "name": "A", "path": [ { "taxiway": "N", "near": [-80.1] }, { "spot": "2" } ] }""", "A", "point 0 near needs [lon, lat]")]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1" }, { "point": [-80.1, 25.1, 3] } ] }""", "A", "point 1 point needs [lon, lat]")]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1", "snap": "foot" }, { "spot": "2" } ] }""", "A", "point 0 has unknown key 'snap'")]
    [InlineData("""{ "name": "A", "path": [ { "spot": " " }, { "spot": "2" } ] }""", "A", "point 0 spot is blank")]
    [InlineData("""{ "name": "A", "path": [ { "taxiway": " ", "near": [-80.1, 25.1] }, { "spot": "2" } ] }""", "A", "point 0 taxiway is blank")]
    [InlineData("""{ "name": "A", "oneWay": true, "path": [ { "spot": "1" }, { "spot": "2" } ] }""", "A", "has unknown key 'oneWay'")]
    [InlineData(
        """{ "name": "A", "path": [ { "junction": ["N", " n "] }, { "spot": "2" } ] }""",
        "A",
        "point 0 junction needs two different taxiway names"
    )]
    [InlineData("""{ "name": "A", "path": [ null, { "spot": "2" } ] }""", "A", "point 0 has no resolver")]
    [InlineData("""{ "name": "rwy10L", "path": [ { "spot": "1" }, { "spot": "2" } ] }""", "rwy10L", "name reserved for runway edges")]
    [InlineData("""{ "name": "N:LINK", "path": [ { "spot": "1" }, { "spot": "2" } ] }""", "N:LINK", "name reserved for runway edges")]
    [InlineData("""{ "name": "A", "path": [ { "spot": "1" }, { "junction": [] } ] }""", "A", "point 1 junction needs two taxiway names")]
    public void SpotEdges_BadEntry_WarnsAndSkipsOnlyThatEntry(string badEntry, string warnedName, string problem)
    {
        (AirportSidecarLoadResult result, string filePath) = LoadSpotEdges($"{badEntry}, {ValidSpotEdge}");

        Assert.Equal([$"{filePath}: spotEdges[0] ({warnedName}): {problem}, skipping"], result.Warnings);
        SpotEdgeChain chain = Assert.Single(Assert.Single(result.Airports).SpotEdges);
        Assert.Equal("RAMP", chain.Name);
        Assert.Equal(["1", "2"], [.. chain.Path.Select(p => p.Spot!)]);
    }

    /// <summary>
    /// A second entry in one file with the same name and the same normalised path warns and replaces the first in its
    /// place; an entry with the same name and another path is a separate chain.
    /// </summary>
    [Fact]
    public void SpotEdges_DuplicateInFile_WarnsAndLastWinsInPlace()
    {
        (AirportSidecarLoadResult result, string filePath) = LoadSpotEdges(
            """
            { "name": "RAMP", "notes": "first", "path": [ { "spot": "1" }, { "spot": "2" } ] },
            { "name": "RAMP", "path": [ { "spot": "3" }, { "spot": "4" } ] },
            { "name": "ramp", "notes": "second", "path": [ { "spot": "$1" }, { "spot": " 2" } ] }
            """
        );

        Assert.Equal([$"{filePath}: spotEdges[2] (ramp): duplicate of spotEdges[0], last wins"], result.Warnings);
        IReadOnlyList<SpotEdgeChain> chains = Assert.Single(result.Airports).SpotEdges;
        Assert.Equal(2, chains.Count);
        Assert.Equal("second", chains[0].Notes);
        Assert.Equal(["1", "2"], [.. chains[0].Path.Select(p => p.Spot!)]);
        Assert.Equal(["3", "4"], [.. chains[1].Path.Select(p => p.Spot!)]);
    }

    /// <summary>A spot name loads upper-cased, so two entries whose spots differ only in case are duplicates.</summary>
    [Fact]
    public void SpotEdges_SpotNames_LoadUpperCased_AndDuplicateCaseInsensitively()
    {
        (AirportSidecarLoadResult result, string filePath) = LoadSpotEdges(
            """
            { "name": "RAMP", "path": [ { "spot": "a1" }, { "spot": "2" } ] },
            { "name": "RAMP", "path": [ { "spot": "$A1" }, { "spot": "2" } ] }
            """
        );

        Assert.Equal([$"{filePath}: spotEdges[1] (RAMP): duplicate of spotEdges[0], last wins"], result.Warnings);
        SpotEdgeChain chain = Assert.Single(Assert.Single(result.Airports).SpotEdges);
        Assert.Equal(["A1", "2"], [.. chain.Path.Select(p => p.Spot!)]);
    }

    /// <summary>Loads one KMIA sidecar file whose <c>spotEdges</c> section holds the given entries, through <c>LoadFiles</c>.</summary>
    private static (AirportSidecarLoadResult Result, string FilePath) LoadSpotEdges(string entries)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        Directory.CreateDirectory(tempDir);
        string filePath = Path.Combine(tempDir, "mia.json");
        try
        {
            File.WriteAllText(filePath, $$"""{ "airportId": "KMIA", "spotEdges": [ {{entries}} ] }""");
            return (AirportSidecarLoader.LoadFiles([filePath]), filePath);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>Loads one KOAK sidecar file with the given content.</summary>
    private static AirportSidecarLoadResult LoadOakSidecar(string json)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "oak.json"), json);
            return AirportSidecarLoader.LoadAll(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    /// <summary>Loads a KSFO sidecar whose only section is one <c>oneWayEdges</c> entry.</summary>
    private static AirportSidecarLoadResult LoadOneWaySidecar(string oneWayEntry)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "sidecar-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "Airports");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "sfo.json"), $$"""{ "airportId": "KSFO", "oneWayEdges": [ {{oneWayEntry}} ] }""");
            return AirportSidecarLoader.LoadAll(tempDir);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
