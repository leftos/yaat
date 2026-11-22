using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views;
using Yaat.Client.Views.Authoring;
using Yaat.Client.Views.Map;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// Tools › Airport Editor…: a click opens one window, a second click brings it forward and a click after it closed opens
/// a new one; opening the committed KOAK map from a file shows its feature count in the status line and its path in the
/// title, fits the view to the map, and draws its taxiways on the canvas; a fit asked before the canvas has a size is
/// made once it has one.
/// </summary>
public class AirportEditorWindowTests
{
    private static readonly string OakPath = Path.Combine(AppContext.BaseDirectory, "TestData", "oak.geojson");

    // The mock's taxiway colour, #ffc83c, and the spot colour, rgb(220,160,40).
    private static readonly SKColor TaxiwayColour = new(0xFF, 0xC8, 0x3C);
    private static readonly SKColor SpotColour = new(220, 160, 40);

    [AvaloniaFact]
    public void MenuItem_OpensOneWindow_ASecondClickActivatesIt_AndAfterCloseAClickOpensANewOne()
    {
        (MainWindow main, _) = MainWindowHost.Boot();
        AirportEditorWindow? first = null;
        AirportEditorWindow? second = null;
        try
        {
            MenuItem item = main.FindControl<MenuItem>("AirportEditorMenuItem")!;
            Assert.True(item.IsEnabled);

            Click(item);
            first = Assert.Single(OpenWindows.All.OfType<AirportEditorWindow>());
            Click(item);
            Assert.Same(first, Assert.Single(OpenWindows.All.OfType<AirportEditorWindow>()));

            first.Close();
            MainWindowHost.Pump();
            Assert.Empty(OpenWindows.All.OfType<AirportEditorWindow>());

            Click(item);
            second = Assert.Single(OpenWindows.All.OfType<AirportEditorWindow>());
            Assert.NotSame(first, second);
        }
        finally
        {
            first?.Close();
            second?.Close();
            MainWindowHost.CloseAll(main);
        }
    }

    [AvaloniaFact]
    public async Task OpeningOakFromAFile_ShowsTheStatusAndTheTitle_AndFitsTheViewToTheMap()
    {
        AirportMapDocument expected = ReadOak(_ => true);
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);

            string status = $"Opened {OakPath}: {expected.Features.Count} features.";
            Assert.Equal(status, window.ViewModel.StatusText);
            Assert.Equal(status, window.FindControl<TextBlock>("StatusText")!.Text);
            Assert.Contains($"Airport Editor — {OakPath}", window.Title, StringComparison.Ordinal);
            AssertFitted(window.FindControl<AirportEditorCanvas>("MapCanvas")!.Viewport, expected);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARenderOfTheWindowWithOakOpen_DrawsTaxiwaysInsideTheCanvas()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            window.Width = 1100;
            window.Height = 700;
            await ShowAndOpenOakAsync(window);

            using SKBitmap frame = window.FindControl<AirportEditorCanvas>("MapCanvas")!.RenderForTesting();

            int taxiwayPixels = CountNear(frame, TaxiwayColour);
            Assert.True(taxiwayPixels >= 50, $"{taxiwayPixels} taxiway-colour pixels drawn");
        }
        finally
        {
            window.Close();
        }
    }

    // The spot colour is near the taxiway colour, so a map of KOAK's spots alone proves the probe counts no spot.
    [AvaloniaFact]
    public void ARenderOfOaksSpotsAlone_HasNoTaxiwayColourPixel()
    {
        var canvas = new AirportEditorCanvas { Document = ReadOak(type => type == "spot") };
        var window = new Window
        {
            Width = 1100,
            Height = 700,
            Content = canvas,
        };
        try
        {
            window.Show();
            MainWindowHost.Pump();
            canvas.FitToFeatures();

            using SKBitmap frame = canvas.RenderForTesting();

            Assert.True(CountNear(frame, SpotColour) > 0);
            Assert.Equal(0, CountNear(frame, TaxiwayColour));
        }
        finally
        {
            window.Close();
            canvas.Dispose();
        }
    }

    [AvaloniaFact]
    public void AFitAskedBeforeTheCanvasHasASize_IsMadeOnceItHasOne()
    {
        AirportMapDocument oak = ReadOak(_ => true);
        var canvas = new AirportEditorCanvas { Document = oak };
        canvas.FitToFeatures();
        Assert.Equal(0, canvas.Viewport.PixelWidth);
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = canvas,
        };
        try
        {
            window.Show();
            MainWindowHost.Pump();

            AssertFitted(canvas.Viewport, oak);
        }
        finally
        {
            window.Close();
            canvas.Dispose();
        }
    }

    private static void AssertFitted(MapViewport viewport, AirportMapDocument map)
    {
        Assert.True(viewport.PixelWidth > 0);
        Assert.True(viewport.PixelHeight > 0);
        foreach (MapPosition position in map.Features.SelectMany(f => f.Coordinates))
        {
            (float x, float y) = viewport.LatLonToScreen(position.Lat, position.Lon);
            Assert.InRange(x, 0, viewport.PixelWidth);
            Assert.InRange(y, 0, viewport.PixelHeight);
        }
    }

    // The committed KOAK map with only the features whose properties.type passes keep.
    private static AirportMapDocument ReadOak(Func<string?, bool> keep)
    {
        JsonNode root = JsonNode.Parse(File.ReadAllText(OakPath))!;
        JsonArray features = root["features"]!.AsArray();
        foreach (JsonNode? feature in features.ToList())
        {
            if (!keep((string?)feature!["properties"]?["type"]))
            {
                features.Remove(feature);
            }
        }

        return AirportMapDocument.Read(root.ToJsonString());
    }

    private static int CountNear(SKBitmap frame, SKColor colour)
    {
        int count = 0;
        for (int y = 0; y < frame.Height; y++)
        {
            for (int x = 0; x < frame.Width; x++)
            {
                SKColor actual = frame.GetPixel(x, y);
                bool near =
                    (Math.Abs(actual.Red - colour.Red) <= 12)
                    && (Math.Abs(actual.Green - colour.Green) <= 12)
                    && (Math.Abs(actual.Blue - colour.Blue) <= 12);
                count += near ? 1 : 0;
            }
        }

        return count;
    }

    private static AirportEditorWindow NewWindow(AirportLayoutDownloader downloader) =>
        new(new UserPreferences(), _ => new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new OakPrompts()));

    private static async Task ShowAndOpenOakAsync(AirportEditorWindow window)
    {
        window.Show();
        MainWindowHost.Pump();
        await window.ViewModel.OpenFileCommand.ExecuteAsync(null);
        MainWindowHost.Pump();
    }

    private static void Click(MenuItem item)
    {
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
    }

    // Answers the file picker with the committed KOAK map; the airport and folder prompts are cancelled.
    private sealed class OakPrompts : IAirportEditorPrompts
    {
        public Task<string?> AskAirportIdAsync() => Task.FromResult<string?>(null);

        public Task<string?> PickMapFileAsync() => Task.FromResult<string?>(OakPath);

        public Task<string?> PickMapFolderAsync() => Task.FromResult<string?>(null);
    }
}
