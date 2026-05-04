using System.Text.Json;
using Xunit;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for TaxiRouteLoader — parses per-airport route JSON files into TaxiRouteDefinition
/// objects with airportId stamped onto each route. Mirrors CustomFixLoader's warn-don't-throw
/// pattern: malformed input adds a warning to the result and skips the offending entry.
/// </summary>
public class TaxiRouteLoaderTests
{
    [Fact]
    public void LoadAll_MissingDirectory_ReturnsWarningAndEmptyRoutes()
    {
        var result = TaxiRouteLoader.LoadAll(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid()));

        Assert.Empty(result.Routes);
        Assert.Single(result.Warnings);
        Assert.Contains("not found", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void LoadAll_BundledTestData_LoadsKoakRoutes()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        var result = TaxiRouteLoader.LoadAll(baseDir);

        // Three routes in koak-routes.json — all load successfully (validation against the
        // airport graph happens lazily at menu-build time, not here).
        Assert.Equal(3, result.Routes.Count);
        Assert.Empty(result.Warnings);

        var dep30 = result.Routes.FirstOrDefault(r => r.Name == "DEP 30 via W");
        Assert.NotNull(dep30);
        Assert.Equal("KOAK", dep30!.AirportId);
        Assert.Equal("W", dep30.Path);
        Assert.Equal("30", dep30.DestinationRunway);

        var dep28L = result.Routes.FirstOrDefault(r => r.Name == "DEP 28L via K-W");
        Assert.NotNull(dep28L);
        Assert.Equal("K W", dep28L!.Path);
        Assert.Equal(["K", "W"], dep28L.GetPathTokens());
    }

    [Fact]
    public void LoadAll_AirportIdStampedFromFile()
    {
        string baseDir = Path.Combine(AppContext.BaseDirectory, "TestData", "ARTCCs");

        var result = TaxiRouteLoader.LoadAll(baseDir);

        // Every route in the bundled fixture is keyed to KOAK (the file's airportId).
        Assert.All(result.Routes, r => Assert.Equal("KOAK", r.AirportId));
    }

    [Fact]
    public void LoadAll_MalformedJson_AddsWarningAndContinues()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "broken.json"), "{ this is not valid json");
            File.WriteAllText(
                Path.Combine(categoryDir, "good.json"),
                """
                {
                  "airportId": "KSFO",
                  "routes": [
                    { "name": "test", "path": "A" }
                  ]
                }
                """
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            // The good file still loads; the broken one adds a warning.
            Assert.Single(result.Routes);
            Assert.Equal("KSFO", result.Routes[0].AirportId);
            Assert.Contains(result.Warnings, w => w.Contains("broken.json", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_MissingAirportId_AddsWarning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "no-airport.json"),
                """
                {
                  "routes": [{ "name": "x", "path": "A" }]
                }
                """
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Empty(result.Routes);
            Assert.Single(result.Warnings);
            Assert.Contains("airportId", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_RouteWithEmptyPath_SkippedWithWarning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "kxxx.json"),
                """
                {
                  "airportId": "KXXX",
                  "routes": [
                    { "name": "good", "path": "A" },
                    { "name": "empty", "path": "   " }
                  ]
                }
                """
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Single(result.Routes);
            Assert.Equal("good", result.Routes[0].Name);
            Assert.Contains(
                result.Warnings,
                w => w.Contains("empty", StringComparison.OrdinalIgnoreCase) || w.Contains("path", StringComparison.OrdinalIgnoreCase)
            );
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_RouteWithMissingName_SkippedWithWarning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "kxxx.json"),
                """
                {
                  "airportId": "KXXX",
                  "routes": [
                    { "path": "A" }
                  ]
                }
                """
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Empty(result.Routes);
            Assert.Single(result.Warnings);
            Assert.Contains("name", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_RouteWithConflictingDestinations_SkippedWithWarning()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "kxxx.json"),
                """
                {
                  "airportId": "KXXX",
                  "routes": [
                    {
                      "name": "ambiguous",
                      "path": "A",
                      "destinationRunway": "10R",
                      "destinationParking": "G7"
                    }
                  ]
                }
                """
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Empty(result.Routes);
            Assert.Single(result.Warnings);
            Assert.Contains("destination", result.Warnings[0], StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_DiscoversRoutesAcrossMultipleArtccs()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string zabDir = Path.Combine(tempDir, "ZAB", "TaxiRoutes");
        string zlaDir = Path.Combine(tempDir, "ZLA", "TaxiRoutes");
        Directory.CreateDirectory(zabDir);
        Directory.CreateDirectory(zlaDir);
        try
        {
            File.WriteAllText(
                Path.Combine(zabDir, "kabq-routes.json"),
                """{ "airportId": "KABQ", "routes": [{ "name": "ABQ test", "path": "A" }] }"""
            );
            File.WriteAllText(
                Path.Combine(zlaDir, "klax-routes.json"),
                """{ "airportId": "KLAX", "routes": [{ "name": "LAX test", "path": "B" }] }"""
            );

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Equal(2, result.Routes.Count);
            Assert.Contains(result.Routes, r => r.AirportId == "KABQ");
            Assert.Contains(result.Routes, r => r.AirportId == "KLAX");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_IgnoresFilesOutsideTaxiRoutesSubfolder()
    {
        // Files in sibling category folders (CustomFixes, FixPronunciations) under the same ARTCC
        // must not be scanned by TaxiRouteLoader — each loader sticks to its own subdirectory.
        string tempDir = Path.Combine(Path.GetTempPath(), "yaat-taxi-routes-test-" + Guid.NewGuid());
        string customFixesDir = Path.Combine(tempDir, "ZTEST", "CustomFixes");
        string taxiRoutesDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(customFixesDir);
        Directory.CreateDirectory(taxiRoutesDir);
        try
        {
            // A custom-fix-shaped JSON in the wrong folder — TaxiRouteLoader must skip this entirely.
            File.WriteAllText(Path.Combine(customFixesDir, "fixes.json"), """[{"name": "X", "aliases": ["X"], "lat": 0, "lon": 0}]""");
            File.WriteAllText(Path.Combine(taxiRoutesDir, "real.json"), """{ "airportId": "KZZZ", "routes": [{ "name": "real", "path": "A" }] }""");

            var result = TaxiRouteLoader.LoadAll(tempDir);

            Assert.Single(result.Routes);
            Assert.Equal("KZZZ", result.Routes[0].AirportId);
            Assert.Empty(result.Warnings);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Theory]
    [InlineData("W", "30", null, null, "TAXI W RWY 30")]
    [InlineData("K W", "28L", null, null, "TAXI K W RWY 28L")]
    [InlineData("A B", null, "G7", null, "TAXI A B @G7")]
    [InlineData("T T3 B", null, null, "S2", "TAXI T T3 B $S2")]
    [InlineData("K W", null, null, null, "TAXI K W")]
    [InlineData("  K   W  ", "28L", null, null, "TAXI K W RWY 28L")]
    public void ToCanonicalCommand_BuildsExpectedString(string path, string? destRunway, string? destParking, string? destSpot, string expected)
    {
        var def = new TaxiRouteDefinition
        {
            Name = "test",
            Path = path,
            DestinationRunway = destRunway,
            DestinationParking = destParking,
            DestinationSpot = destSpot,
        };

        Assert.Equal(expected, def.ToCanonicalCommand());
    }

    [Theory]
    [InlineData("T T3 B", new[] { "T", "T3", "B" })]
    [InlineData("W", new[] { "W" })]
    [InlineData("  K   W  ", new[] { "K", "W" })]
    [InlineData("\tA\tB\t", new[] { "A", "B" })]
    [InlineData("", new string[0])]
    [InlineData("   ", new string[0])]
    public void GetPathTokens_SplitsOnWhitespace(string path, string[] expected)
    {
        var def = new TaxiRouteDefinition { Name = "test", Path = path };
        Assert.Equal(expected, def.GetPathTokens());
    }

    [Fact]
    public void ToCanonicalCommand_RoundTripsThroughGroundCommandParser()
    {
        // The canonical command emitted by a preset must re-parse via the standard
        // GroundCommandParser into a TaxiCommand with the same path/destination, so the
        // server's command pipeline accepts it without needing a preset-aware parser.
        var def = new TaxiRouteDefinition
        {
            Name = "DEP 10R via T-T3-B",
            Path = "T T3 B",
            DestinationRunway = "10R",
        };

        string canonical = def.ToCanonicalCommand();
        Assert.Equal("TAXI T T3 B RWY 10R", canonical);

        // Strip the leading "TAXI " — the parser API takes the argument portion.
        var result = GroundCommandParser.ParseTaxi(canonical["TAXI ".Length..]);

        Assert.True(result.IsSuccess, $"Parse failed: {result.Reason}");
        var taxi = (TaxiCommand)result.Value!;
        Assert.Equal(["T", "T3", "B"], taxi.Path);
        Assert.Equal("10R", taxi.DestinationRunway);
    }

    [Fact]
    public void TaxiRouteDefinition_RoundTripsThroughJson()
    {
        var def = new TaxiRouteDefinition
        {
            Name = "DEP 28R via B-K",
            Path = "B K",
            DestinationRunway = "28R",
            Tags = ["dep", "28R"],
        };

        string json = JsonSerializer.Serialize(def, new JsonSerializerOptions { WriteIndented = false });
        var parsed = JsonSerializer.Deserialize<TaxiRouteDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(parsed);
        Assert.Equal(def.Name, parsed!.Name);
        Assert.Equal(def.Path, parsed.Path);
        Assert.Equal(def.DestinationRunway, parsed.DestinationRunway);
        Assert.Equal(def.Tags, parsed.Tags);
    }
}
