using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Threading;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.GuideCapture.Capture;
using Yaat.Sim;
using Yaat.Sim.Weather;

namespace Yaat.GuideCapture.Scenes;

// Feature showcase > the METAR window naming the GFS wind grid the weather flies on. The OAK
// scenario's room, then a real ZOA weather file (docs/atctrainer-weather-examples) saved with
// the committed GFS grid over ZOA (tests/Yaat.Sim.Tests/TestData/gfs-zoa-sample.grib2) embedded
// in it, loaded through the client's own saved-weather path so the header's second line is that
// code's output. The profile gains the 0 ft surface layer the server's live-weather rule needs,
// carrying the KOAK METAR's own surface wind. The METAR window is opened as MainWindow does,
// once the load's line has landed.
internal sealed class WhatsNewWindsAloftSourceScene : ScenarioSceneBase
{
    private const string WeatherFileName = "01HTY06CDMYXR5BS55E22SGAF2.json";
    private const string WeatherFileFolder = "atctrainer-weather-examples";
    private const string GridFileName = "gfs-zoa-sample.grib2";
    private const string SurfaceStation = "KOAK";

    private MetarWindow? _metarWindow;
    private string? _stagedWeatherPath;

    public override string Name => "whats-new-winds-aloft-source";

    protected override int TabIndex => 0;

    protected override async Task OnSceneReadyAsync(Window window, MainViewModel vm, CaptureContext ctx)
    {
        _stagedWeatherPath = await StageWeatherFileAsync(ctx);
        await vm.LoadWeatherCommand.ExecuteAsync(_stagedWeatherPath);
        await SceneActions.WaitUntilAsync(
            () =>
                (vm.WindsAloftLine?.Contains("NOAA GFS", StringComparison.Ordinal) == true)
                && (vm.Metars.Count > 0)
                && (vm.ActiveWeatherName is not null),
            TimeSpan.FromSeconds(30),
            "the saved weather's GFS winds-aloft line"
        );
        Console.WriteLine($"  header: METAR  {vm.ActiveWeatherName}");
        Console.WriteLine($"          {vm.WindsAloftLine}");

        _metarWindow = new MetarWindow(vm.Preferences) { DataContext = vm };
        _metarWindow.Show();
        Dispatcher.UIThread.RunJobs();
        _metarWindow.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    public override Window GetCaptureTarget(Window primary) => _metarWindow ?? primary;

    public override IEnumerable<Window> ExtraWindows => _metarWindow is null ? [] : [_metarWindow];

    public override void AfterCapture()
    {
        if (_stagedWeatherPath is { } path && File.Exists(path))
        {
            File.Delete(path);
        }

        _stagedWeatherPath = null;
    }

    // The ZOA weather file with the grid saved into it: its surface layer, the grid's hash so the
    // load does not report a mismatched one, then the grid's own bytes, written where the client's
    // saved-weather load can read it.
    private async Task<string> StageWeatherFileAsync(CaptureContext ctx)
    {
        string weatherPath = Path.Combine(ctx.RepoRoot, "docs", WeatherFileFolder, WeatherFileName);
        string gridPath = Path.Combine(ctx.RepoRoot, "tests", "Yaat.Sim.Tests", "TestData", GridFileName);
        byte[] grib2 = await File.ReadAllBytesAsync(gridPath);
        string json = AddSurfaceLayer(await File.ReadAllTextAsync(weatherPath));
        string staged = SavedWeatherGrid.Embed(SavedWeatherGrid.WithHash(json, WindGridRegistry.HashOf(grib2)), grib2);

        string folder = Path.Combine(ctx.RepoRoot, ".tmp");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, $"{Name}-weather.json");
        await File.WriteAllTextAsync(path, staged);
        Console.WriteLine($"  staged weather: {path}");
        return path;
    }

    // The profile with the METAR surface layer at 0 ft that the grid's column sits above, built
    // from the station's own METAR: the server attaches a grid only to live-shaped weather.
    private static string AddSurfaceLayer(string json)
    {
        JsonObject root =
            JsonNode.Parse(json) as JsonObject
            ?? throw new InvalidOperationException($"The ZOA weather file {WeatherFileName} is not a JSON object.");
        List<string> metars = [.. (root["metars"] as JsonArray ?? []).Select(m => m?.GetValue<string>()).OfType<string>()];
        MetarParser.ParsedMetar station =
            MetarParser.FindStation(metars, SurfaceStation)
            ?? throw new InvalidOperationException(
                $"The ZOA weather file {WeatherFileName} has no {SurfaceStation} METAR to take the surface wind from."
            );

        var surface = new JsonObject
        {
            ["altitude"] = JsonValue.Create(0),
            ["direction"] = JsonValue.Create(
                station.WindDirectionDeg ?? throw new InvalidOperationException($"{SurfaceStation}'s METAR reports no surface wind direction.")
            ),
            ["speed"] = JsonValue.Create(
                station.WindSpeedKts ?? throw new InvalidOperationException($"{SurfaceStation}'s METAR reports no surface wind speed.")
            ),
        };
        if (station.WindGustKts is { } gusts)
        {
            surface["gusts"] = JsonValue.Create(gusts);
        }

        JsonArray layers = root["windLayers"] as JsonArray ?? [];
        layers.Insert(0, surface);
        root["windLayers"] = layers;
        return root.ToJsonString();
    }
}
