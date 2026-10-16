using System.Text.Json.Nodes;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Sim;

namespace Yaat.Client.Tests;

/// <summary>
/// What the METAR header's "Winds aloft:" line and the status bar's final message say for each state a weather can load in:
/// the GFS grid, the FD fallback with its reason, METAR surface winds only, a saved file's grid or its absence, and the room's
/// weather as every member sees it.
/// </summary>
public class WindsAloftSourceTests
{
    private const string LiveName = "Live Weather (15:52Z)";
    private static readonly DateTime Cycle12Z16Oct = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);
    private const string Grid = "NOAA GFS 12Z 16 Oct, +4 h forecast";

    [Fact]
    public void SourceLabel_NamesTheRunAndTheForecastHourInPlainWords() => Assert.Equal(Grid, GfsGridFetcher.SourceLabel(Cycle12Z16Oct, 4));

    [Fact]
    public void SourceLabel_SingleDigitDayAndAnalysisHour_HasNoLeadingZeroes()
    {
        var cycle = new DateTime(2026, 11, 3, 0, 0, 0, DateTimeKind.Utc);

        Assert.Equal("NOAA GFS 00Z 3 Nov, +0 h forecast", GfsGridFetcher.SourceLabel(cycle, 0));
    }

    [Fact]
    public void FdUnavailableWarning_IsTheTerminalLine() =>
        Assert.Equal("Live weather: FD winds aloft unavailable; METAR surface winds only.", GfsGridFetcher.FdUnavailableWarning);

    [Fact]
    public void WarningText_WithFdLayers_FliesTheFdForecastOnly() =>
        Assert.Equal(
            "Live weather: GFS winds unavailable — grid not in the saved file; using FD winds aloft only.",
            GfsGridFetcher.WarningText(SavedWeatherGrid.NotInSavedFile, hasFdLayers: true)
        );

    [Fact]
    public void WarningText_WithoutFdLayers_SaysMetarSurfaceWindsOnly() =>
        Assert.Equal(
            "Live weather: GFS winds unavailable — grid not in the saved file; METAR surface winds only.",
            GfsGridFetcher.WarningText(SavedWeatherGrid.NotInSavedFile, hasFdLayers: false)
        );

    // ── (a)-(c): a live load, as the loader sees it ─────────────

    [Fact]
    public void LiveLoad_GridLoaded_NamesTheGrid()
    {
        WindsAloftReport report = WindsAloftSource.ForLiveLoad(LiveWeather(withFd: true), new WindGridOutcome(SampleGrid(), null));

        Assert.Equal($"Winds aloft: {Grid}", report.Line);
        Assert.Equal($"Loaded: {LiveName} · winds aloft: {Grid}", report.Status);
        Assert.Null(report.Warning);
    }

    [Fact]
    public void LiveLoad_GfsFailed_NamesTheFdForecastAndTheReason()
    {
        var outcome = new WindGridOutcome(null, GfsGridFetcher.RequestFailed);

        WindsAloftReport report = WindsAloftSource.ForLiveLoad(LiveWeather(withFd: true), outcome);

        Assert.Equal("Winds aloft: FAA FD forecast · GFS unavailable (NOMADS request failed)", report.Line);
        Assert.Equal($"Loaded: {LiveName} · winds aloft: FAA FD (GFS unavailable)", report.Status);
        Assert.Null(report.Warning);
    }

    [Fact]
    public void LiveLoad_GfsAndFdFailed_SaysMetarSurfaceWindsOnly()
    {
        WeatherProfile weather = LiveWeather(withFd: false);
        var outcome = new WindGridOutcome(null, GfsGridFetcher.RequestFailed);

        WindsAloftReport report = WindsAloftSource.ForLiveLoad(weather, outcome);

        Assert.Equal("Winds aloft: none · METAR surface winds only", report.Line);
        Assert.Equal($"Loaded: {LiveName} · no winds aloft, METAR surface winds only", report.Status);
        Assert.Equal("Live weather: FD winds aloft unavailable; METAR surface winds only.", report.Warning);
        Assert.False(WindsAloftSource.HasFdLayers(weather));
    }

    [Fact]
    public void LiveLoad_FdLayersPresent_HasFdLayers() => Assert.True(WindsAloftSource.HasFdLayers(LiveWeather(withFd: true)));

    [Fact]
    public void LiveLoad_WithoutMetars_FliesTheFdForecastWithoutAReason()
    {
        var weather = new WeatherProfile
        {
            Name = LiveName,
            WindLayers =
            [
                new WindLayer
                {
                    Altitude = 6000,
                    Direction = 290,
                    Speed = 25,
                },
            ],
        };

        WindsAloftReport report = WindsAloftSource.ForLiveLoad(weather, WindGridOutcome.None);

        Assert.Equal("Winds aloft: FAA FD forecast", report.Line);
        Assert.Equal($"Loaded: {LiveName} · winds aloft: FAA FD", report.Status);
        Assert.Null(report.Warning);
    }

    // ── (d)-(e): a saved or API weather file ───────────────────

    [Fact]
    public void SavedLoad_GridInTheFile_NamesTheGridFromTheSavedFile()
    {
        WindsAloftReport? report = WindsAloftSource.ForSavedLoad(LiveName, new WindGridOutcome(SampleGrid(), null), hasFdLayers: true);

        Assert.NotNull(report);
        Assert.Equal($"Winds aloft: {Grid} · saved file", report.Line);
        Assert.Equal($"Weather loaded: {LiveName} · winds aloft: {Grid} (saved file)", report.Status);
    }

    [Fact]
    public void SavedLoad_GridMissingFromTheFile_NamesTheFdForecastAndWhy()
    {
        WindsAloftReport? report = WindsAloftSource.ForSavedLoad(
            LiveName,
            new WindGridOutcome(null, SavedWeatherGrid.NotInSavedFile),
            hasFdLayers: true
        );

        Assert.NotNull(report);
        Assert.Equal("Winds aloft: FAA FD forecast · GFS grid not in the saved file", report.Line);
        Assert.Equal($"Weather loaded: {LiveName} · winds aloft: FAA FD (GFS grid not in the saved file)", report.Status);
    }

    [Fact]
    public void SavedLoad_GridRefused_NamesTheFdForecastAndTheReason()
    {
        WindsAloftReport? report = WindsAloftSource.ForSavedLoad(
            LiveName,
            new WindGridOutcome(null, GfsGridFetcher.OverUploadLimit),
            hasFdLayers: true
        );

        Assert.NotNull(report);
        Assert.Equal("Winds aloft: FAA FD forecast · GFS unavailable (grid over the upload limit)", report.Line);
        Assert.Equal($"Weather loaded: {LiveName} · winds aloft: FAA FD (GFS unavailable)", report.Status);
    }

    [Fact]
    public void SavedLoad_GridMissing_NoFdLayers_SaysMetarSurfaceWindsOnly()
    {
        WindsAloftReport? report = WindsAloftSource.ForSavedLoad(
            LiveName,
            new WindGridOutcome(null, SavedWeatherGrid.NotInSavedFile),
            hasFdLayers: false
        );

        Assert.NotNull(report);
        Assert.Equal("Winds aloft: none · METAR surface winds only · GFS grid not in the saved file", report.Line);
        Assert.Equal($"Weather loaded: {LiveName} · no winds aloft, METAR surface winds only (GFS grid not in the saved file)", report.Status);
        Assert.Null(report.Warning);
    }

    [Fact]
    public void SavedLoad_GridRefused_NoFdLayers_SaysMetarSurfaceWindsOnlyAndTheReason()
    {
        WindsAloftReport? report = WindsAloftSource.ForSavedLoad(
            LiveName,
            new WindGridOutcome(null, GfsGridFetcher.OverUploadLimit),
            hasFdLayers: false
        );

        Assert.NotNull(report);
        Assert.Equal("Winds aloft: none · METAR surface winds only · GFS unavailable (grid over the upload limit)", report.Line);
        Assert.Equal($"Weather loaded: {LiveName} · no winds aloft, METAR surface winds only (GFS unavailable)", report.Status);
        Assert.Null(report.Warning);
    }

    [Fact]
    public void SavedLoad_NoGridNamed_LeavesTheLineToTheRoomWeather() =>
        Assert.Null(WindsAloftSource.ForSavedLoad("OAK West Gusty", WindGridOutcome.None, hasFdLayers: true));

    // ── The room's weather, as every member sees it ─────────────

    [Fact]
    public void RoomWeather_WithTheGridsRun_NamesTheGrid() =>
        Assert.Equal($"Winds aloft: {Grid}", WindsAloftSource.ForRoomWeather(LiveDto(withFd: true, Cycle12Z16Oct, 4), playback: false));

    [Fact]
    public void RoomWeather_ReplayWithTheGridsRun_NamesTheRecordedGrid() =>
        Assert.Equal($"Winds aloft: {Grid} · recorded", WindsAloftSource.ForRoomWeather(LiveDto(withFd: true, Cycle12Z16Oct, 4), playback: true));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RoomWeather_LiveShapedWithoutAGrid_NamesTheFdForecast(bool playback) =>
        Assert.Equal("Winds aloft: FAA FD forecast", WindsAloftSource.ForRoomWeather(LiveDto(withFd: true, null, null), playback));

    [Fact]
    public void RoomWeather_LiveShapedWithOnlyTheSurfaceLayer_SaysMetarSurfaceWindsOnly() =>
        Assert.Equal(
            "Winds aloft: none · METAR surface winds only",
            WindsAloftSource.ForRoomWeather(LiveDto(withFd: false, null, null), playback: false)
        );

    [Fact]
    public void RoomWeather_NotLiveShaped_IsScenarioWeather()
    {
        var dto = new WeatherChangedDto("OAK West Gusty", [new WindLayerDto(3000, 280, 25, 35)], null, [Metar], "{}", null, null);

        Assert.Equal("Winds aloft: scenario weather", WindsAloftSource.ForRoomWeather(dto, playback: false));
    }

    [Fact]
    public void RoomWeather_Cleared_HasNoLine() =>
        Assert.Null(WindsAloftSource.ForRoomWeather(new WeatherChangedDto(null, null, null, null, null, null, null), playback: false));

    [Fact]
    public void RoomWeather_SurfaceLayerNotFirst_IsScenarioWeather()
    {
        var dto = new WeatherChangedDto(
            "OAK West Gusty",
            [new WindLayerDto(3000, 280, 25, null), new WindLayerDto(0, 270, 10, null)],
            null,
            [Metar],
            "{}",
            null,
            null
        );

        Assert.Equal("Winds aloft: scenario weather", WindsAloftSource.ForRoomWeather(dto, playback: false));
    }

    // ── The weather's name and a saved grid's run ──────────────

    [Theory]
    [InlineData("""{"name":"Live Weather (15:52Z)","metars":[],"windLayers":[]}""", "Live Weather (15:52Z)")]
    [InlineData("""{"name":"Timeline","periods":[]}""", "Timeline")]
    [InlineData("""{"metars":[],"windLayers":[]}""", null)]
    public void NameOf_IsTheWeathersOwnName(string json, string? expected) => Assert.Equal(expected, WindsAloftSource.NameOf(json));

    // ── Whether a saved weather JSON carries FD layers ─────────

    [Fact]
    public void HasFdLayers_RealSavedFile_IsTrue() => Assert.True(WindsAloftSource.HasFdLayers(ZoaWeatherJson()));

    [Fact]
    public void HasFdLayers_SavedFileWithoutLayersAboveTheSurface_IsFalse() =>
        Assert.False(WindsAloftSource.HasFdLayers(WithoutFdLayers(ZoaWeatherJson())));

    [Fact]
    public void HasFdLayers_TimelineJson_IsTrue() =>
        Assert.True(
            WindsAloftSource.HasFdLayers(
                """{"name":"Timeline","periods":[{"startMinutes":0,"windLayers":[{"altitude":0,"direction":300,"speed":10}]}]}"""
            )
        );

    [Fact]
    public void HasFdLayers_ProfileWithoutWindLayers_IsFalse() => Assert.False(WindsAloftSource.HasFdLayers("""{"name":"X"}"""));

    [Theory]
    [InlineData("""{"WindLayers":[{"Altitude":3000,"Direction":200,"Speed":20}]}""", true)]
    [InlineData("""{"windLayers":null}""", false)]
    [InlineData("""{"windLayers":{}}""", false)]
    public void HasFdLayers_MatchesTheServersOwnV1Keys(string json, bool expected) => Assert.Equal(expected, WindsAloftSource.HasFdLayers(json));

    [Fact]
    public void ActiveWindGrid_FromGrib2_ReadsTheRunFromTheBytes()
    {
        byte[] grib2 = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "TestData/gfs-zoa-sample.grib2"));

        var grid = ActiveWindGrid.FromGrib2(grib2, "hash");

        Assert.Equal(new DateTime(2026, 10, 14, 12, 0, 0, DateTimeKind.Utc), grid.CycleUtc);
        Assert.Equal(0, grid.ForecastHours);
        Assert.Equal("hash", grid.Hash);
    }

    private const string ZoaWeatherFile = "01HTY06CDMYXR5BS55E22SGAF2.json";

    /// <summary>The real ZOA weather example (a profile: <c>name</c>, <c>windLayers</c>, <c>metars</c>) from the checkout.</summary>
    private static string ZoaWeatherJson() => File.ReadAllText(Path.Combine(FindRepoRoot(), "docs", "atctrainer-weather-examples", ZoaWeatherFile));

    /// <summary>The same JSON with every layer above the 0 ft surface layer removed: METAR surface winds only.</summary>
    private static string WithoutFdLayers(string json)
    {
        JsonObject root = JsonNode.Parse(json) as JsonObject ?? throw new InvalidOperationException("The ZOA weather example is not a JSON object.");
        JsonArray layers =
            root["windLayers"] as JsonArray
            ?? throw new InvalidOperationException("The ZOA weather example has no windLayers array to strip the FD layers from.");
        foreach (JsonNode layer in layers.OfType<JsonNode>().Where(l => (AltitudeOf(l) > 0)).ToList())
        {
            layers.Remove(layer);
        }

        root["windLayers"] = layers;
        return root.ToJsonString();
    }

    private static double AltitudeOf(JsonNode layer) =>
        layer["altitude"]?.GetValue<double>()
        ?? throw new InvalidOperationException("A ZOA weather example layer has no altitude to compare with the surface layer.");

    /// <summary>The checkout this test assembly was built in: the nearest folder above the output that holds yaat.slnx.</summary>
    private static string FindRepoRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "yaat.slnx")))
            {
                return dir.FullName;
            }
        }

        throw new InvalidOperationException($"No yaat.slnx above {AppContext.BaseDirectory}: the ZOA weather example is read from the checkout.");
    }

    private const string Metar = "METAR KOAK 161453Z 28012KT 10SM FEW010 18/11 A3000";

    private static ActiveWindGrid SampleGrid() => new([1, 2, 3], "hash", Cycle12Z16Oct, 4);

    private static WeatherProfile LiveWeather(bool withFd)
    {
        List<WindLayer> layers =
        [
            new WindLayer
            {
                Altitude = 0,
                Direction = 280,
                Speed = 12,
            },
        ];
        if (withFd)
        {
            layers.Add(
                new WindLayer
                {
                    Altitude = 6000,
                    Direction = 290,
                    Speed = 25,
                }
            );
        }

        return new WeatherProfile
        {
            Name = LiveName,
            Metars = [Metar],
            WindLayers = layers,
        };
    }

    private static WeatherChangedDto LiveDto(bool withFd, DateTime? cycleUtc, int? forecastHours)
    {
        List<WindLayerDto> layers = [new WindLayerDto(0, 280, 12, null)];
        if (withFd)
        {
            layers.Add(new WindLayerDto(6000, 290, 25, null));
        }

        return new WeatherChangedDto(LiveName, layers, null, [Metar], "{}", cycleUtc, forecastHours);
    }
}
