using Xunit;
using Yaat.Sim.Data;

namespace Yaat.Sim.Tests;

/// <summary>
/// Tests for <see cref="AvoidTaxiwayLoader"/> — parses per-airport avoided-taxiway JSON files into
/// <see cref="AvoidTaxiwayAirport"/> records. Mirrors <c>TaxiRouteLoaderTests</c>' warn-don't-throw,
/// one-object-per-file conventions.
/// </summary>
public class AvoidTaxiwayLoaderTests
{
    [Fact]
    public void LoadAll_MissingDirectory_ReturnsWarningNoThrow()
    {
        var result = AvoidTaxiwayLoader.LoadAll(Path.Combine(Path.GetTempPath(), "definitely-not-a-real-dir-" + Guid.NewGuid()));

        Assert.Empty(result.Airports);
        Assert.Single(result.Warnings);
        Assert.Contains("not found", result.Warnings[0]);
    }

    [Fact]
    public void LoadAll_ReadsAvoidedTaxiwaysForAirport()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "avoid-twy-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "AvoidTaxiways");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(
                Path.Combine(categoryDir, "oak.json"),
                """{ "airportId": "KOAK", "taxiways": [ { "name": "S", "notes": "ramp lead" }, { "name": "z" } ] }"""
            );

            var result = AvoidTaxiwayLoader.LoadAll(tempDir);

            Assert.Empty(result.Warnings);
            var airport = Assert.Single(result.Airports);
            Assert.Equal("KOAK", airport.AirportId);
            // Names are upper-cased and trimmed at load.
            Assert.Equal(new[] { "S", "Z" }, airport.Taxiways.Select(t => t.Name).ToArray());
            Assert.Equal("ramp lead", airport.Taxiways[0].Notes);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_WarnsOnMissingAirportId()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "avoid-twy-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "AvoidTaxiways");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "bad.json"), """{ "taxiways": [ { "name": "S" } ] }""");

            var result = AvoidTaxiwayLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
            Assert.Contains(result.Warnings, w => w.Contains("missing airportId"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_SkipsEntryWithMissingName_AndWarnsWhenNoneRemain()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "avoid-twy-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "AvoidTaxiways");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "empty.json"), """{ "airportId": "KOAK", "taxiways": [ { "name": "  " } ] }""");

            var result = AvoidTaxiwayLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
            Assert.Contains(result.Warnings, w => w.Contains("missing taxiway name"));
            Assert.Contains(result.Warnings, w => w.Contains("no valid taxiways"));
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_DedupesNamesWithinFile()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "avoid-twy-" + Guid.NewGuid());
        string categoryDir = Path.Combine(tempDir, "ZTEST", "AvoidTaxiways");
        Directory.CreateDirectory(categoryDir);
        try
        {
            File.WriteAllText(Path.Combine(categoryDir, "dup.json"), """{ "airportId": "KOAK", "taxiways": [ { "name": "S" }, { "name": "s" } ] }""");

            var result = AvoidTaxiwayLoader.LoadAll(tempDir);

            var airport = Assert.Single(result.Airports);
            Assert.Equal(new[] { "S" }, airport.Taxiways.Select(t => t.Name).ToArray());
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void LoadAll_IgnoresFilesOutsideAvoidTaxiwaysSubfolder()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "avoid-twy-" + Guid.NewGuid());
        // A file in the TaxiRoutes folder must not be scanned by the AvoidTaxiways loader.
        string otherDir = Path.Combine(tempDir, "ZTEST", "TaxiRoutes");
        Directory.CreateDirectory(otherDir);
        try
        {
            File.WriteAllText(Path.Combine(otherDir, "oak.json"), """{ "airportId": "KOAK", "taxiways": [ { "name": "S" } ] }""");

            var result = AvoidTaxiwayLoader.LoadAll(tempDir);

            Assert.Empty(result.Airports);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
