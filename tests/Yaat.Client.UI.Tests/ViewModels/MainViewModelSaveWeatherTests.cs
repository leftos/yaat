using Avalonia.Headless.XUnit;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels;

namespace Yaat.Client.UI.Tests.ViewModels;

/// <summary>
/// Save Weather As on a client holding no bytes for the grid the room's weather names: a view model that never connected cannot
/// fetch the grid, so the file is written without the hash and the terminal says so.
/// </summary>
public class MainViewModelSaveWeatherTests
{
    private const string Metar = "METAR KOAK 161453Z 28012KT 10SM FEW010 18/11 A3000";

    private const string GridHash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string LiveJson =
        "{\"name\":\"Live Weather (15:52Z)\",\"metars\":[\""
        + Metar
        + "\"],\"windLayers\":[{\"altitude\":0,\"direction\":280,\"speed\":12}],"
        + "\"windGridHash\":\""
        + GridHash
        + "\"}";

    [AvaloniaFact]
    public async Task SaveWeather_NotConnected_WritesTheFileWithoutTheHashAndWarns()
    {
        // The save adds a recent-weather entry, which writes the process's shared preferences.json.
        using var scope = new PreferencesFileScope();
        string folder = Path.Combine(Path.GetTempPath(), $"yaat-weather-save-test-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "live-saved.json");

        try
        {
            Directory.CreateDirectory(folder);
            var picker = new FakeFilePickerService();
            picker.QueueSaveFile(path);
            var vm = new MainViewModel(picker);
            vm.ApplyWeatherChanged(
                new WeatherChangedDto("Live Weather (15:52Z)", [new WindLayerDto(0, 280, 12, null)], null, [Metar], LiveJson, null, null)
            );

            await vm.SaveWeatherCommand.ExecuteAsync(null);

            Assert.True(File.Exists(path));
            string contents = await File.ReadAllTextAsync(path);
            Assert.Null(SavedWeatherGrid.NamedHash(contents));
            Assert.Contains("Live Weather (15:52Z)", contents);
            Assert.Contains(
                vm.TerminalEntries,
                e => (e.Kind == TerminalEntryKind.Warning) && (e.Message == "Saved without its GFS grid: not connected to the server")
            );
            Assert.Contains(vm.Preferences.RecentWeatherFiles, r => (r.FilePath == path) && (r.Name == "live-saved"));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }
}
