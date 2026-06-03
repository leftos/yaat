using Xunit;
using Yaat.Sim.Data;

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
        var result = AirportSidecarLoader.LoadAll(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid()));

        Assert.Empty(result.Airports);
        Assert.Single(result.Warnings);
        Assert.Contains("not found", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAll_BundledTestData_LoadsKoakRoutes()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        var result = AirportSidecarLoader.LoadAll(baseDir);

        var oak = Assert.Single(result.Airports, a => a.AirportId == "KOAK");
        // Three routes in the bundled oak.json — all load (graph validation happens later, at menu-build time).
        Assert.Equal(3, oak.TaxiRoutes.Count);
        Assert.Empty(result.Warnings);

        var dep30 = oak.TaxiRoutes.FirstOrDefault(r => r.Name == "DEP 30 via W");
        Assert.NotNull(dep30);
        Assert.Equal("KOAK", dep30!.AirportId);
        Assert.Equal("W", dep30.Path);
        Assert.Equal("30", dep30.DestinationRunway);

        var dep28L = oak.TaxiRoutes.FirstOrDefault(r => r.Name == "DEP 28L via K-W");
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Empty(result.Warnings);
            var airport = Assert.Single(result.Airports);
            Assert.Equal("KOAK", airport.AirportId);
            // avoidTaxiways names are upper-cased and trimmed at load.
            Assert.Equal(["S", "Z"], airport.AvoidTaxiways.Select(t => t.Name).ToArray());
            Assert.Equal("ramp lead", airport.AvoidTaxiways[0].Notes);
            var route = Assert.Single(airport.TaxiRoutes);
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
            Assert.Equal(["S"], airport.AvoidTaxiways.Select(t => t.Name).ToArray());
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
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

            var result = AirportSidecarLoader.LoadAll(tempDir);

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

            var result = AirportSidecarLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
