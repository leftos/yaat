using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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

    // The selection highlight's colour, #FFC107.
    private static readonly SKColor AmberColour = new(0xFF, 0xC1, 0x07);

    // A canvas corner the fit leaves empty: the fit pads the map's box by 5% of the canvas on each side.
    private static readonly Point EmptyCorner = new(3, 3);

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

    [AvaloniaFact]
    public async Task ALeftClickOnAFeature_FillsThePropertyPanel_AndEscClearsIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            ItemsControl rows = window.FindControl<ItemsControl>("PropertyRows")!;
            TextBlock none = window.FindControl<TextBlock>("NoSelectionText")!;
            Assert.True(none.IsVisible);

            Press(window, canvas, at, MouseButton.Left);

            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            Assert.Null(window.ViewModel.SelectedVertexIndex);
            Assert.Same(window.ViewModel.SelectionRows, rows.ItemsSource);
            Assert.Equal(new AirportEditorPropertyRow("Type", "parking"), window.ViewModel.SelectionRows[1]);
            Assert.NotEmpty(rows.GetRealizedContainers());
            Assert.False(none.IsVisible);
            Assert.Equal(target, canvas.Selection);

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Null(window.ViewModel.SelectedFeatureIndex);
            Assert.Empty(window.ViewModel.SelectionRows);
            Assert.True(none.IsVisible);
            Assert.Null(canvas.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALeftClickOnEmptySpace_ClearsTheSelection()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point at) = ParkingUnderTheCursor(window, canvas);
            Press(window, canvas, at, MouseButton.Left);
            Assert.NotNull(window.ViewModel.SelectedFeatureIndex);

            Press(window, canvas, EmptyCorner, MouseButton.Left);

            Assert.Null(window.ViewModel.SelectedFeatureIndex);
            Assert.Empty(window.ViewModel.SelectionRows);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRightClickMenu_OnAFeature_AndOnEmptySpace_HasExactlyItsItems()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);

            Press(window, canvas, at, MouseButton.Right);

            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            Assert.Equal(["Zoom to feature", "Copy coordinates", "-", "Fit to airport"], Labels(canvas.ActiveContextMenu!));
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();

            Press(window, canvas, EmptyCorner, MouseButton.Right);

            Assert.Null(window.ViewModel.SelectedFeatureIndex);
            Assert.Equal(["Copy coordinates", "-", "Fit to airport"], Labels(canvas.ActiveContextMenu!));
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CopyCoordinates_PutsThePositionUnderTheClickOnTheClipboard()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            string? copied = null;
            canvas.ClipboardWriter = text =>
            {
                copied = text;
                return Task.CompletedTask;
            };
            var at = new Point(200, 150);
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon(200, 150);

            Press(window, canvas, at, MouseButton.Right);
            ClickItem(canvas.ActiveContextMenu!, "Copy coordinates");

            Assert.Equal(string.Create(CultureInfo.InvariantCulture, $"{lat:F6}, {lon:F6}"), copied);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ZoomToFeature_CentresTheViewOnTheFeaturesBox_AndFitsALine()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point at) = ParkingUnderTheCursor(window, canvas);
            double zoom = canvas.Viewport.Zoom;

            Press(window, canvas, at, MouseButton.Right);
            ClickItem(canvas.ActiveContextMenu!, "Zoom to feature");

            AssertCentredOn(canvas.Viewport, window.ViewModel.SelectedFeature!);
            Assert.Equal(zoom, canvas.Viewport.Zoom);

            int taxiwayIndex = window.ViewModel.Document!.Features.Select((f, i) => (f, i)).First(x => x.f.FeatureType == "taxiway").i;
            MapFeature taxiway = window.ViewModel.Document!.Features[taxiwayIndex];
            canvas.ZoomToFeature(taxiwayIndex);

            AssertCentredOn(canvas.Viewport, taxiway);
            Assert.NotEqual(zoom, canvas.Viewport.Zoom);
            foreach (MapPosition position in taxiway.Coordinates)
            {
                (float x, float y) = canvas.Viewport.LatLonToScreen(position.Lat, position.Lon);
                Assert.InRange(x, 0, canvas.Viewport.PixelWidth);
                Assert.InRange(y, 0, canvas.Viewport.PixelHeight);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARenderWithASelectedPoint_DrawsAnAmberRingAroundIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point at) = ParkingUnderTheCursor(window, canvas);
            Press(window, canvas, at, MouseButton.Left);

            using SKBitmap frame = canvas.RenderForTesting();

            int amber = 0;
            for (int i = 0; i < 16; i++)
            {
                double angle = i * Math.PI / 8;
                int x = (int)Math.Round(at.X + (6.6 * Math.Cos(angle)));
                int y = (int)Math.Round(at.Y + (6.6 * Math.Sin(angle)));
                amber += IsNear(frame.GetPixel(x, y), AmberColour) ? 1 : 0;
            }

            Assert.True(amber >= 8, $"{amber} of 16 ring samples amber");
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EscAfterARightClickSelection_ClearsIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            Press(window, canvas, at, MouseButton.Right);
            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Null(window.ViewModel.SelectedFeatureIndex);
            Assert.Null(canvas.Selection);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task SelectingALine_UpdatesTheHoverToItsVertex_WithoutAPointerMove()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit vertex, Point at) = TaxiwayVertexUnderTheCursor(window, canvas);
            window.MouseMove(canvas.TranslatePoint(at, window)!.Value);
            MainWindowHost.Pump();
            Assert.Equal(new AirportEditorHit(vertex.FeatureIndex, null), canvas.Hover);

            Press(window, canvas, at, MouseButton.Left);

            Assert.Equal(new AirportEditorHit(vertex.FeatureIndex, null), canvas.Selection);
            Assert.Equal(vertex, canvas.Hover);
        }
        finally
        {
            window.Close();
        }
    }

    // A taxiway vertex whose own pixel hits the taxiway with nothing selected, and that vertex with the taxiway selected.
    private static (AirportEditorHit Vertex, Point At) TaxiwayVertexUnderTheCursor(AirportEditorWindow window, AirportEditorCanvas canvas)
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Taxiway))
        {
            for (int k = 0; k < shape.Positions.Length; k++)
            {
                (float x, float y) = canvas.Viewport.LatLonToScreen(shape.Positions[k].Lat, shape.Positions[k].Lon);
                Point at = new(Math.Round(x), Math.Round(y));
                var vertex = new AirportEditorHit(shape.FeatureIndex, k);
                bool fits =
                    (
                        AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, null)
                        == new AirportEditorHit(shape.FeatureIndex, null)
                    ) && (AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, shape.FeatureIndex) == vertex);
                if (fits)
                {
                    return (vertex, at);
                }
            }
        }

        throw new InvalidOperationException("No taxiway vertex under its own pixel");
    }

    private static void AssertCentredOn(MapViewport viewport, MapFeature feature)
    {
        AirportEditorBounds box = AirportEditorBounds.Of(feature.Coordinates)!;
        Assert.Equal((box.MinLat + box.MaxLat) / 2, viewport.CenterLat, 1e-6);
        Assert.Equal((box.MinLon + box.MaxLon) / 2, viewport.CenterLon, 1e-6);
    }

    // The first parking point whose own pixel hits it, in canvas coordinates.
    private static (AirportEditorHit Hit, Point At) ParkingUnderTheCursor(AirportEditorWindow window, AirportEditorCanvas canvas)
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Parking))
        {
            (float x, float y) = canvas.Viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
            Point at = new(Math.Round(x), Math.Round(y));
            if (
                (AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, null) is { } hit)
                && (hit == new AirportEditorHit(shape.FeatureIndex, null))
            )
            {
                return (hit, at);
            }
        }

        throw new InvalidOperationException("No parking point under its own pixel");
    }

    // A press and release of one button at a point of the canvas, unmoved.
    private static void Press(Window window, AirportEditorCanvas canvas, Point at, MouseButton button)
    {
        Point inWindow = canvas.TranslatePoint(at, window)!.Value;
        window.MouseMove(inWindow);
        MainWindowHost.Pump();
        window.MouseDown(inWindow, button);
        MainWindowHost.Pump();
        window.MouseUp(inWindow, button);
        MainWindowHost.Pump();
    }

    private static void ClickItem(ContextMenu menu, string header)
    {
        MenuItem item = menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == header);
        item.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        MainWindowHost.Pump();
        menu.Close();
        MainWindowHost.Pump();
    }

    private static List<string> Labels(ContextMenu menu) =>
        [
            .. menu.Items.Select(i =>
                i switch
                {
                    Separator => "-",
                    MenuItem m => m.Header as string ?? "",
                    _ => i?.GetType().Name ?? "null",
                }
            ),
        ];

    private static bool IsNear(SKColor actual, SKColor colour) =>
        (Math.Abs(actual.Red - colour.Red) <= 12) && (Math.Abs(actual.Green - colour.Green) <= 12) && (Math.Abs(actual.Blue - colour.Blue) <= 12);

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
