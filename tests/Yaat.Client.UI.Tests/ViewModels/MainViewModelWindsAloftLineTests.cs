using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// The METAR header's "Winds aloft:" line follows the room's weather: another member's load, a rejoin and a replay set it from
/// <see cref="WeatherChangedDto"/>, clearing the weather clears it, and the loader's own fuller report (with the GFS failure
/// reason) outlives the room's echo of that same weather.
/// </summary>
public class MainViewModelWindsAloftLineTests
{
    private const string Metar = "METAR KOAK 161453Z 28012KT 10SM FEW010 18/11 A3000";
    private const string LiveJson = "{\"name\":\"Live Weather (15:52Z)\"}";
    private static readonly DateTime Cycle = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    [AvaloniaFact]
    public void WeatherChanged_FromAnotherMemberWithTheGridsRun_ShowsTheGrid()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void WeatherChanged_DuringPlayback_ShowsTheRecordedGrid()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { IsPlaybackMode = true };

        vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast · recorded", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void WeatherCleared_ClearsTheLine()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

        vm.ApplyWeatherChanged(new WeatherChangedDto(null, null, null, null, null, null, null));

        Assert.Null(vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void LoadersReport_SetsTheLineAndTheStatusBar()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);

        Assert.Equal(FdFallbackReport.Line, vm.WindsAloftLine);
        Assert.Equal(FdFallbackReport.Status, vm.StatusText);
    }

    [AvaloniaFact]
    public void LoadersReport_OutlivesTheRoomsEchoOfTheSameWeather()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);
        vm.ApplyWeatherChanged(LiveDto(LiveJson, null, null));

        Assert.Equal(FdFallbackReport.Line, vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void LoadersReport_ReplacesAnEchoThatArrivedFirst()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ApplyWeatherChanged(LiveDto(LiveJson, null, null));
        Assert.Equal("Winds aloft: FAA FD forecast", vm.WindsAloftLine);

        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);

        Assert.Equal(FdFallbackReport.Line, vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void OtherWeather_ReplacesTheLoadersReport()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);

        vm.ApplyWeatherChanged(LiveDto("{\"name\":\"Live Weather (16:40Z)\"}", Cycle, 4));

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void LoadersReport_DoesNotOutliveADifferentWeatherCarryingTheSameJson()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);

        vm.ApplyWeatherChanged(new WeatherChangedDto("OAK West Gusty", [new WindLayerDto(3000, 280, 25, null)], null, [Metar], LiveJson, null, null));

        Assert.Equal("Winds aloft: scenario weather", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void LoadersReport_ThenOwnSavedLoadWithoutAGrid_FollowsTheNewWeather()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        const string scenarioJson = "{\"name\":\"OAK West Gusty\"}";
        vm.ApplyLoadedWindsAloft(LiveJson, FdFallbackReport);
        vm.ApplyWeatherChanged(LiveDto(LiveJson, null, null));

        vm.ReportSavedWindsAloft(new PreparedWeather(scenarioJson, WindGridOutcome.None), "OAK West Gusty", "Weather loaded: OAK West Gusty");
        vm.ApplyWeatherChanged(
            new WeatherChangedDto("OAK West Gusty", [new WindLayerDto(3000, 280, 25, null)], null, [Metar], scenarioJson, null, null)
        );

        Assert.Equal("Winds aloft: scenario weather", vm.WindsAloftLine);
        Assert.Equal("Weather loaded: OAK West Gusty", vm.StatusText);
    }

    // ── A saved load without FD layers says METAR surface winds only ──

    [AvaloniaFact]
    public void SavedLoadWithoutFdLayers_GridMissing_ShowsTheMetarOnlyLine()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ReportSavedWindsAloft(
            new PreparedWeather(SurfaceOnlyJson, new WindGridOutcome(null, SavedWeatherGrid.NotInSavedFile)),
            "c1-zoa-8-saved",
            "Weather loaded"
        );

        Assert.Equal("Winds aloft: none · METAR surface winds only · GFS grid not in the saved file", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public async Task SavedLoadWithoutFdLayers_GridMissing_WarnsMetarSurfaceWindsOnly()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        await vm.PrepareSavedWeatherAsync(NamedGridWithoutFdLayersJson);

        TerminalEntry warning = Assert.Single(vm.TerminalEntries, e => (e.Kind == TerminalEntryKind.Warning));
        Assert.Equal("Live weather: GFS winds unavailable — grid not in the saved file; METAR surface winds only.", warning.Message);
    }

    [AvaloniaFact]
    public async Task SavedLoadWithoutFdLayers_GridOverTheUploadLimit_WarnsMetarSurfaceWindsOnly()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        string json = SavedWeatherGrid.Embed(SurfaceOnlyJson, new byte[GfsGridFetcher.MaxWindGridUploadBytes + 1]);

        await vm.PrepareSavedWeatherAsync(json);

        TerminalEntry warning = Assert.Single(vm.TerminalEntries, e => (e.Kind == TerminalEntryKind.Warning));
        Assert.EndsWith("grid over the upload limit; METAR surface winds only.", warning.Message);
    }

    // ── A saved load's name: the weather's own, else the call site's fallback ──

    [AvaloniaFact]
    public void SavedLoad_JSONNameDiffersFromTheFallback_ShowsTheWeathersOwnName()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ReportSavedWindsAloft(new PreparedWeather(WgustyJson, new WindGridOutcome(SampleGrid, null)), "oak-west-gusty-saved", "Weather loaded");

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast · saved file", vm.WindsAloftLine);
        Assert.Equal("Weather loaded: OAK West Gusty · winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast (saved file)", vm.StatusText);
    }

    [AvaloniaFact]
    public void SavedLoad_JSONWithoutAName_ShowsTheCallSitesFallback()
    {
        var vm = new MainViewModel(new FakeFilePickerService());

        vm.ReportSavedWindsAloft(
            new PreparedWeather("{\"metars\":[]}", new WindGridOutcome(SampleGrid, null)),
            "oak-west-gusty-saved",
            "Weather loaded"
        );

        Assert.StartsWith("Weather loaded: oak-west-gusty-saved ", vm.StatusText);
    }

    [AvaloniaFact]
    public void PlaybackEnds_DropsTheRecordedSuffix()
    {
        var vm = new MainViewModel(new FakeFilePickerService()) { IsPlaybackMode = true };
        vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

        vm.IsPlaybackMode = false;

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void PlaybackStartsAfterTheWeather_AddsTheRecordedSuffix()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

        vm.IsPlaybackMode = true;

        Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast · recorded", vm.WindsAloftLine);
    }

    [AvaloniaFact]
    public void MetarHeader_ShowsTheLineAndHidesItWhenEmpty()
    {
        var vm = new MainViewModel(new FakeFilePickerService());
        var view = new MetarView();
        var window = new Window
        {
            Width = 520,
            Height = 300,
            Content = view,
            DataContext = vm,
        };
        window.ShowAndRunLayout();
        try
        {
            TextBlock line = view.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Name == "WindsAloftLineText");
            Assert.False(line.IsVisible);

            vm.ApplyWeatherChanged(LiveDto(LiveJson, Cycle, 4));

            Assert.True(line.IsVisible);
            Assert.Equal("Winds aloft: NOAA GFS 12Z 16 Oct, +4 h forecast", line.Text);

            vm.ApplyWeatherChanged(new WeatherChangedDto(null, null, null, null, null, null, null));

            Assert.False(line.IsVisible);
        }
        finally
        {
            window.Close();
        }
    }

    private const string WgustyJson = "{\"name\":\"OAK West Gusty\"}";

    // A live-shaped saved profile carrying only the 0 ft surface layer: no FD winds aloft.
    private const string SurfaceOnlyJson = "{\"name\":\"C1-ZOA-8\",\"windLayers\":[{\"altitude\":0,\"direction\":207.2,\"speed\":10}]}";

    // The same, naming a grid whose bytes it does not carry, so the load reports them missing.
    private const string NamedGridWithoutFdLayersJson =
        "{\"name\":\"C1-ZOA-8\",\"windGridHash\":\"abc\",\"windLayers\":[{\"altitude\":0,\"direction\":207.2,\"speed\":10}]}";

    private static readonly ActiveWindGrid SampleGrid = new([1, 2, 3], "hash", Cycle, 4);

    private static readonly WindsAloftReport FdFallbackReport = new(
        "Winds aloft: FAA FD forecast · GFS unavailable (NOMADS request failed)",
        "Loaded: Live Weather (15:52Z) · winds aloft: FAA FD (GFS unavailable)",
        null
    );

    private static WeatherChangedDto LiveDto(string sourceJson, DateTime? cycleUtc, int? forecastHours) =>
        new(
            "Live Weather (15:52Z)",
            [new WindLayerDto(0, 280, 12, null), new WindLayerDto(6000, 290, 25, null)],
            null,
            [Metar],
            sourceJson,
            cycleUtc,
            forecastHours
        );
}
