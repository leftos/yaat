using System.Text.Json;
using Xunit;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// Tests against cached real aviationweather.gov data (ZOA, 2026-03-06 ~20Z).
/// Data files in TestData/ — METARs and FD winds.
/// </summary>
public class LiveWeatherRealDataTests
{
    private static readonly string TestDataDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "TestData");

    private static string ReadTestFile(string name) => File.ReadAllText(Path.Combine(TestDataDir, name));

    // -------------------------------------------------------------------------
    // METAR parsing from real JSON
    // -------------------------------------------------------------------------

    [Fact]
    public void RealMetars_AllParseable()
    {
        var json = ReadTestFile("zoa_metars.json");
        var metars = JsonSerializer.Deserialize<List<MetarJsonDto>>(json, JsonOpts)!;
        Assert.NotEmpty(metars);

        foreach (var m in metars)
        {
            Assert.False(string.IsNullOrWhiteSpace(m.RawOb), $"Empty rawOb for {m.IcaoId}");
            var parsed = MetarParser.Parse(m.RawOb);
            Assert.NotNull(parsed);
            Assert.Equal(m.IcaoId, parsed.StationId);
            Assert.NotNull(parsed.VisibilityStatuteMiles);
        }
    }

    [Fact]
    public void RealMetars_SfoHasCorrectStation()
    {
        var json = ReadTestFile("zoa_metars.json");
        var metars = JsonSerializer.Deserialize<List<MetarJsonDto>>(json, JsonOpts)!;

        var sfoRaw = metars.First(m => m.IcaoId == "KSFO").RawOb;
        var parsed = MetarParser.Parse(sfoRaw);
        Assert.NotNull(parsed);
        Assert.Equal("KSFO", parsed.StationId);
    }

    // -------------------------------------------------------------------------
    // FD winds parsing from real text
    // -------------------------------------------------------------------------

    [Fact]
    public void RealFdWinds_ParsesStations()
    {
        var text = ReadTestFile("zoa_fd_winds.txt");
        var stations = WindsAloftParser.Parse(text);
        Assert.NotNull(stations);
        Assert.NotEmpty(stations);

        // SFO region should have recognizable stations
        var stationIds = stations.Select(s => s.StationId).ToHashSet();
        Assert.True(stationIds.Count >= 3, $"Only {stationIds.Count} stations parsed");
    }

    [Fact]
    public void RealFdWinds_StationsHaveWindData()
    {
        var text = ReadTestFile("zoa_fd_winds.txt");
        var stations = WindsAloftParser.Parse(text)!;

        foreach (var station in stations)
        {
            Assert.NotEmpty(station.Winds);
            foreach (var wind in station.Winds)
            {
                Assert.True(wind.AltitudeFt >= 3000 && wind.AltitudeFt <= 39000, $"Unexpected altitude {wind.AltitudeFt} for {station.StationId}");
                if (!wind.IsLightVariable)
                {
                    Assert.True(
                        wind.DirectionTrue >= 0 && wind.DirectionTrue <= 360,
                        $"Bad direction {wind.DirectionTrue} for {station.StationId} at {wind.AltitudeFt}"
                    );
                    Assert.True(
                        wind.SpeedKts >= 0 && wind.SpeedKts <= 300,
                        $"Bad speed {wind.SpeedKts} for {station.StationId} at {wind.AltitudeFt}"
                    );
                }
            }
        }
    }

    [Fact]
    public void RealFdWinds_StandardLevelsPresent()
    {
        var text = ReadTestFile("zoa_fd_winds.txt");
        var stations = WindsAloftParser.Parse(text)!;

        var allLevels = stations.SelectMany(s => s.Winds).Select(w => w.AltitudeFt).Distinct().Order().ToList();

        // Should have at least some standard levels
        Assert.Contains(6000, allLevels);
        Assert.Contains(9000, allLevels);
        Assert.Contains(12000, allLevels);
    }

    // -------------------------------------------------------------------------
    // MetarInterpolator with real METARs
    // -------------------------------------------------------------------------

    [Fact]
    public void RealMetars_FindStation_SfoMatch()
    {
        var json = ReadTestFile("zoa_metars.json");
        var metars = JsonSerializer.Deserialize<List<MetarJsonDto>>(json, JsonOpts)!;
        var rawMetars = metars.Select(m => m.RawOb).ToList();

        var result = MetarParser.FindStation(rawMetars, "SFO");
        Assert.NotNull(result);
        Assert.Equal("KSFO", result.StationId);
    }

    [Fact]
    public void RealMetars_FindStation_OakMatch()
    {
        var json = ReadTestFile("zoa_metars.json");
        var metars = JsonSerializer.Deserialize<List<MetarJsonDto>>(json, JsonOpts)!;
        var rawMetars = metars.Select(m => m.RawOb).ToList();

        var result = MetarParser.FindStation(rawMetars, "OAK");
        Assert.NotNull(result);
        Assert.Equal("KOAK", result.StationId);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private sealed class MetarJsonDto
    {
        public string IcaoId { get; set; } = "";
        public string RawOb { get; set; } = "";
    }
}
