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
