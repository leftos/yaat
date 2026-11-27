using System.Globalization;
using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.Services.Imagery;
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

    // A Settings change of the imagery toggle or its brightness reaches an open editor once it comes forward.
    [AvaloniaFact]
    public void ActivatingTheWindow_ReadsTheImageryToggleAndBrightnessFromThePreferences()
    {
        var preferences = new UserPreferences();
        bool show = preferences.AirportEditorShowImagery;
        int brightness = preferences.AirportEditorImageryBrightness;
        using var downloader = new AirportLayoutDownloader();
        var tiles = new TileStub(serveFirst: 0);
        var window = new AirportEditorWindow(
            preferences,
            _ => new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new OakPrompts(), NoAirportImagery.Instance, null),
            tiles.CreateCache
        );
        try
        {
            window.Show();
            MainWindowHost.Pump();
            preferences.SetAirportEditorShowImagery(!show);
            preferences.SetAirportEditorImageryBrightness(brightness == 30 ? 40 : 30);

            window.Activate();
            MainWindowHost.Pump();

            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            Assert.Equal(!show, window.ViewModel.ShowImagery);
            Assert.Equal(!show, window.FindControl<ToggleButton>("ImageryToggle")!.IsChecked);
            Assert.Equal(!show, canvas.ShowImagery);
            Assert.Equal(preferences.AirportEditorImageryBrightness, canvas.ImageryBrightness);
        }
        finally
        {
            window.Close();
            preferences.SetAirportEditorShowImagery(show);
            preferences.SetAirportEditorImageryBrightness(brightness);
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
            Assert.Equal(["Zoom to feature", "Copy coordinates", "-", "Fit to airport", "-", "Delete"], Labels(canvas.ActiveContextMenu!));
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
    private static (AirportEditorHit Vertex, Point At) TaxiwayVertexUnderTheCursor(AirportEditorWindow window, AirportEditorCanvas canvas) =>
        TaxiwayVertexWhere(window, canvas, (_, _) => true);

    // As TaxiwayVertexUnderTheCursor, for the first vertex k of a taxiway shape that pick accepts.
    private static (AirportEditorHit Vertex, Point At) TaxiwayVertexWhere(
        AirportEditorWindow window,
        AirportEditorCanvas canvas,
        Func<AirportEditorShape, int, bool> pick
    )
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Taxiway))
        {
            for (int k = 0; k < shape.Positions.Length; k++)
            {
                if (!pick(shape, k))
                {
                    continue;
                }

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

    [AvaloniaFact]
    public async Task CtrlZ_UndoesADelete_AndCtrlY_RedoesIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            int count = window.ViewModel.Document!.Features.Count;
            Press(window, canvas, at, MouseButton.Left);
            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);

            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Equal(count - 1, window.ViewModel.Document!.Features.Count);
            Assert.Same(window.ViewModel.Document, canvas.Document);
            Assert.True(window.FindControl<Button>("UndoButton")!.IsEnabled);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Equal(count, window.ViewModel.Document!.Features.Count);
            Assert.Same(window.ViewModel.Document, canvas.Document);
            Assert.True(window.FindControl<Button>("RedoButton")!.IsEnabled);

            window.KeyPressQwerty(PhysicalKey.Y, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Equal(count - 1, window.ViewModel.Document!.Features.Count);
            Assert.Same(window.ViewModel.Document, canvas.Document);
        }
        finally
        {
            window.Close();
        }
    }

    // A read-only property row's TextBox marks Delete and its undo keys handled; the window still acts on them.
    [AvaloniaFact]
    public async Task DelAndCtrlZ_FromAFocusedPropertyRow_DeleteTheFeature_ThenRestoreIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            int count = window.ViewModel.Document!.Features.Count;
            Press(window, canvas, at, MouseButton.Left);
            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            TextBox row = window.FindControl<ItemsControl>("PropertyRows")!.GetVisualDescendants().OfType<TextBox>().First();
            row.Focus();
            MainWindowHost.Pump();
            Assert.True(row.IsFocused);

            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Equal(count - 1, window.ViewModel.Document!.Features.Count);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Equal(count, window.ViewModel.Document!.Features.Count);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ADragOfAVertex_MovesIt_AndOneUndoRestoresIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit vertex, Point at) = TaxiwayVertexUnderTheCursor(window, canvas);
            Press(window, canvas, at, MouseButton.Left);
            Assert.Equal(new AirportEditorHit(vertex.FeatureIndex, null), canvas.Selection);
            AirportMapDocument opened = window.ViewModel.Document!;
            Point to = at + new Vector(25, 15);
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)to.X, (float)to.Y);
            var exclusion = new SnapExclusion(vertex.FeatureIndex, vertex.VertexIndex!.Value);
            Assert.Null(SnapEngine.Find(opened, new MapPosition(lon, lat), SnapOptions.Default, exclusion, null));

            DragLeft(window, canvas, at, to);

            MapPosition moved = window.ViewModel.Document!.Features[vertex.FeatureIndex].Coordinates[vertex.VertexIndex.Value];
            Assert.Equal(lat, moved.Lat, 1e-9);
            Assert.Equal(lon, moved.Lon, 1e-9);
            Assert.Same(window.ViewModel.Document, canvas.Document);
            Assert.Equal("Undo Move vertex", window.ViewModel.UndoDescription);
            Assert.Equal(vertex, canvas.Selection);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Same(opened, window.ViewModel.Document);
            Assert.Same(opened, canvas.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APressThatMovesWithinTheTapSize_SelectsAndEditsNothing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;

            DragLeft(window, canvas, at, at + new Vector(1, 1));

            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ALeftDragOnEmptySpace_NeitherPansNorSelects()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point parking) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            double centerLat = canvas.Viewport.CenterLat;
            double centerLon = canvas.Viewport.CenterLon;

            DragLeft(window, canvas, EmptyCorner, parking);

            Assert.Equal(centerLat, canvas.Viewport.CenterLat);
            Assert.Equal(centerLon, canvas.Viewport.CenterLon);
            Assert.Null(window.ViewModel.SelectedFeatureIndex);
            Assert.Null(canvas.Selection);
            Assert.Same(opened, window.ViewModel.Document);
        }
        finally
        {
            window.Close();
        }
    }

    // Zoomed to about a metre a pixel, a parking point dragged to within a pixel of its nearest neighbour snaps to it: the
    // crosshair is drawn on the neighbour during the drag, and the release puts the point exactly there.
    [AvaloniaFact]
    public async Task TheSnapCrosshair_IsDrawnDuringADrag_NearASnapTarget()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorShape dragged, AirportEditorShape target, Point from, Point to) = ParkingNextToAnother(window, canvas);
            Point fromInWindow = canvas.TranslatePoint(from, window)!.Value;
            Point toInWindow = canvas.TranslatePoint(to, window)!.Value;
            window.MouseMove(fromInWindow);
            MainWindowHost.Pump();
            window.MouseDown(fromInWindow, MouseButton.Left);
            MainWindowHost.Pump();
            window.MouseMove(toInWindow, RawInputModifiers.LeftMouseButton);
            MainWindowHost.Pump();

            AirportEditorRenderSnapshot frame = canvas.SnapshotForTesting()!;
            Assert.Equal(target.Positions[0], frame.SnapAt);
            Assert.Equal(target.Positions[0], frame.Scene.Shapes.Single(s => s.FeatureIndex == dragged.FeatureIndex).Positions[0]);
            using (SKBitmap bitmap = canvas.RenderForTesting())
            {
                Assert.True(CountNear(bitmap, AirportEditorRenderer.SnapColour) > 0);
            }

            window.MouseUp(toInWindow, MouseButton.Left);
            MainWindowHost.Pump();

            Assert.Equal(target.Positions[0], window.ViewModel.Document!.Features[dragged.FeatureIndex].Coordinates[0]);
            Assert.Equal("Undo Move feature", window.ViewModel.UndoDescription);
            Assert.Null(canvas.SnapshotForTesting()!.SnapAt);
        }
        finally
        {
            window.Close();
        }
    }

    // A parking point and its nearest parking neighbour 30 to 200 px away on a view centred on the first at zoom 20; the
    // press is on the first's own pixel and the drag ends one pixel right of the neighbour's.
    private static (AirportEditorShape Dragged, AirportEditorShape Target, Point From, Point To) ParkingNextToAnother(
        AirportEditorWindow window,
        AirportEditorCanvas canvas
    )
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        List<AirportEditorShape> parking = [.. scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Parking)];
        foreach (AirportEditorShape shape in parking)
        {
            canvas.Viewport.Zoom = 20;
            canvas.Viewport.CenterLat = shape.Positions[0].Lat;
            canvas.Viewport.CenterLon = shape.Positions[0].Lon;
            (float x, float y) = canvas.Viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
            Point from = new(Math.Round(x), Math.Round(y));
            AirportEditorShape nearest = parking.Where(p => !ReferenceEquals(p, shape)).MinBy(p => ScreenDistance(canvas, from, p))!;
            double distance = ScreenDistance(canvas, from, nearest);
            bool fits =
                (distance >= 30)
                && (distance <= 200)
                && (
                    AirportEditorHitTest.Find(scene, canvas.Viewport, (float)from.X, (float)from.Y, null)
                    == new AirportEditorHit(shape.FeatureIndex, null)
                );
            if (fits)
            {
                (float tx, float ty) = canvas.Viewport.LatLonToScreen(nearest.Positions[0].Lat, nearest.Positions[0].Lon);
                return (shape, nearest, from, new Point(Math.Round(tx) + 1, Math.Round(ty)));
            }
        }

        throw new InvalidOperationException("No parking point with a neighbour 30 to 200 px away");
    }

    private static double ScreenDistance(AirportEditorCanvas canvas, Point from, AirportEditorShape shape)
    {
        (float x, float y) = canvas.Viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
        return Math.Sqrt(((x - from.X) * (x - from.X)) + ((y - from.Y) * (y - from.Y)));
    }

    // A left press at one point of the canvas, a move halfway and then to another with the button held, and the release there.
    private static void DragLeft(Window window, AirportEditorCanvas canvas, Point from, Point to)
    {
        Point a = canvas.TranslatePoint(from, window)!.Value;
        Point b = canvas.TranslatePoint(to, window)!.Value;
        window.MouseMove(a);
        MainWindowHost.Pump();
        window.MouseDown(a, MouseButton.Left);
        MainWindowHost.Pump();
        window.MouseMove(new Point((a.X + b.X) / 2, (a.Y + b.Y) / 2), RawInputModifiers.LeftMouseButton);
        MainWindowHost.Pump();
        window.MouseMove(b, RawInputModifiers.LeftMouseButton);
        MainWindowHost.Pump();
        window.MouseUp(b, MouseButton.Left);
        MainWindowHost.Pump();
    }

    [AvaloniaFact]
    public async Task TheRightClickMenu_OnATaxiwaySegment_InsertsAVertex_AndSplitsAsOneUndoStep()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (int feature, int segment, Point at) = TaxiwaySegmentUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            MapFeature taxiway = opened.Features[feature];

            Press(window, canvas, at, MouseButton.Right);

            List<string> expected =
            [
                "Zoom to feature",
                "Copy coordinates",
                "-",
                "Fit to airport",
                "-",
                "Insert Vertex Here",
                "Split Taxiway Here",
                "Delete",
            ];
            Assert.Equal(expected, Labels(canvas.ActiveContextMenu!));
            ClickItem(canvas.ActiveContextMenu!, "Insert Vertex Here");

            MapFeature inserted = window.ViewModel.Document!.Features[feature];
            Assert.Equal(taxiway.Coordinates.Length + 1, inserted.Coordinates.Length);
            Assert.Equal(taxiway.Coordinates[segment], inserted.Coordinates[segment]);
            Assert.Equal(taxiway.Coordinates[segment + 1], inserted.Coordinates[segment + 2]);
            Assert.Equal("Undo Insert vertex", window.ViewModel.UndoDescription);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();
            Assert.Same(opened, window.ViewModel.Document);

            Press(window, canvas, at, MouseButton.Right);
            ClickItem(canvas.ActiveContextMenu!, "Split Taxiway Here");

            Assert.Equal(opened.Features.Count + 1, window.ViewModel.Document!.Features.Count);
            Assert.Equal(feature, window.ViewModel.SelectedFeatureIndex);
            Assert.Equal(segment + 2, window.ViewModel.Document!.Features[feature].Coordinates.Length);
            Assert.Equal("Undo Split taxiway", window.ViewModel.UndoDescription);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();
            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRightClickMenu_OnAVertex_SplitsAtAnInteriorOne_NotAtAnEnd_AndDeletesTheVertexOrTheFeature()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            AirportMapDocument opened = window.ViewModel.Document!;
            (AirportEditorHit vertex, Point at) = TaxiwayVertexWhere(window, canvas, (s, k) => (k > 0) && (k < s.Positions.Length - 1));
            int count = opened.Features[vertex.FeatureIndex].Coordinates.Length;
            Press(window, canvas, at, MouseButton.Left);

            Press(window, canvas, at, MouseButton.Right);

            Assert.Equal(vertex, canvas.Selection);
            List<string> expected =
            [
                "Zoom to feature",
                "Copy coordinates",
                "-",
                "Fit to airport",
                "-",
                "Delete Vertex",
                "Split Taxiway Here",
                "Delete",
            ];
            Assert.Equal(expected, Labels(canvas.ActiveContextMenu!));
            Assert.True(Item(canvas.ActiveContextMenu!, "Delete Vertex").IsEnabled);
            Assert.True(Item(canvas.ActiveContextMenu!, "Split Taxiway Here").IsEnabled);
            ClickItem(canvas.ActiveContextMenu!, "Split Taxiway Here");

            Assert.Equal(opened.Features.Count + 1, window.ViewModel.Document!.Features.Count);
            Assert.Equal(vertex.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
            Assert.Equal(vertex.VertexIndex + 1, window.ViewModel.Document!.Features[vertex.FeatureIndex].Coordinates.Length);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();
            Assert.Same(opened, window.ViewModel.Document);

            Press(window, canvas, at, MouseButton.Left);
            Press(window, canvas, at, MouseButton.Left);
            Press(window, canvas, at, MouseButton.Right);
            ClickItem(canvas.ActiveContextMenu!, "Delete Vertex");

            Assert.Equal(count - 1, window.ViewModel.Document!.Features[vertex.FeatureIndex].Coordinates.Length);
            Assert.Equal("Undo Delete vertex", window.ViewModel.UndoDescription);
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();
            Assert.Same(opened, window.ViewModel.Document);

            (AirportEditorHit end, Point endAt) = TaxiwayVertexWhere(window, canvas, (_, k) => k == 0);
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();
            Press(window, canvas, endAt, MouseButton.Left);
            Press(window, canvas, endAt, MouseButton.Right);

            Assert.Equal(end, canvas.Selection);
            Assert.False(Item(canvas.ActiveContextMenu!, "Split Taxiway Here").IsEnabled);
            ClickItem(canvas.ActiveContextMenu!, "Delete");

            Assert.Equal(opened.Features.Count - 1, window.ViewModel.Document!.Features.Count);
            Assert.Equal("Undo Delete feature", window.ViewModel.UndoDescription);
        }
        finally
        {
            window.Close();
        }
    }

    // The middle of a taxiway segment at least 40 px long whose own pixel hits the taxiway with nothing selected, with that
    // segment the nearest.
    private static (int Feature, int Segment, Point At) TaxiwaySegmentUnderTheCursor(AirportEditorWindow window, AirportEditorCanvas canvas) =>
        LineSegmentUnderTheCursor(window, canvas, AirportEditorFeatureKind.Taxiway);

    // As TaxiwaySegmentUnderTheCursor, for a line of the given kind.
    private static (int Feature, int Segment, Point At) LineSegmentUnderTheCursor(
        AirportEditorWindow window,
        AirportEditorCanvas canvas,
        AirportEditorFeatureKind kind
    )
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == kind))
        {
            for (int k = 0; k < shape.Positions.Length - 1; k++)
            {
                (float ax, float ay) = canvas.Viewport.LatLonToScreen(shape.Positions[k].Lat, shape.Positions[k].Lon);
                (float bx, float by) = canvas.Viewport.LatLonToScreen(shape.Positions[k + 1].Lat, shape.Positions[k + 1].Lon);
                Point at = new(Math.Round((ax + bx) / 2), Math.Round((ay + by) / 2));
                bool fits =
                    (Math.Sqrt(((bx - ax) * (bx - ax)) + ((by - ay) * (by - ay))) >= 40)
                    && (
                        AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, null)
                        == new AirportEditorHit(shape.FeatureIndex, null)
                    )
                    && (AirportEditorHitTest.NearestSegment(scene, canvas.Viewport, (float)at.X, (float)at.Y, shape.FeatureIndex)?.SegmentIndex == k);
                if (fits)
                {
                    return (shape.FeatureIndex, k, at);
                }
            }
        }

        throw new InvalidOperationException($"No {kind} segment under its own pixel");
    }

    private static MenuItem Item(ContextMenu menu, string header) => menu.Items.OfType<MenuItem>().Single(i => (i.Header as string) == header);

    // A drag past the tap size, then the case's interruption (Del, Ctrl+Z or Esc) before the release.
    private static void StartDragAndHold(Window window, AirportEditorCanvas canvas, Point from, Point to)
    {
        Point a = canvas.TranslatePoint(from, window)!.Value;
        Point b = canvas.TranslatePoint(to, window)!.Value;
        window.MouseMove(a);
        MainWindowHost.Pump();
        window.MouseDown(a, MouseButton.Left);
        MainWindowHost.Pump();
        window.MouseMove(b, RawInputModifiers.LeftMouseButton);
        MainWindowHost.Pump();
    }

    private static void Release(Window window, AirportEditorCanvas canvas, Point at)
    {
        window.MouseUp(canvas.TranslatePoint(at, window)!.Value, MouseButton.Left);
        MainWindowHost.Pump();
    }

    [AvaloniaFact]
    public async Task ADelDuringADrag_CancelsIt_AndTheReleaseAppliesNothing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit last, Point at) = TaxiwayVertexWhere(
                window,
                canvas,
                (s, k) => (s.Positions.Length > 2) && (k == s.Positions.Length - 1)
            );
            Press(window, canvas, at, MouseButton.Left);
            StartDragAndHold(window, canvas, at, at + new Vector(20, 10));
            Assert.Equal(last, canvas.Selection);

            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            MainWindowHost.Pump();
            AirportMapDocument deleted = window.ViewModel.Document!;

            Assert.NotNull(canvas.SnapshotForTesting());
            using (SKBitmap frame = canvas.RenderForTesting())
            {
                Assert.True(frame.Width > 0);
            }

            Release(window, canvas, at + new Vector(20, 10));

            Assert.Same(deleted, window.ViewModel.Document);
            Assert.Equal("Undo Delete vertex", window.ViewModel.UndoDescription);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ACtrlZDuringADrag_CancelsIt_AndTheReleaseAppliesNothing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point at) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            DragLeft(window, canvas, at, at + new Vector(30, 0));
            Assert.Equal("Undo Move feature", window.ViewModel.UndoDescription);
            StartDragAndHold(window, canvas, at + new Vector(30, 0), at + new Vector(30, 30));

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();
            Assert.NotNull(canvas.SnapshotForTesting());
            Release(window, canvas, at + new Vector(30, 30));

            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
            Assert.Equal("Redo Move feature", window.ViewModel.RedoDescription);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EscDuringADrag_CancelsIt_AndKeepsTheSelection()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            StartDragAndHold(window, canvas, at, at + new Vector(30, 10));

            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();
            Release(window, canvas, at + new Vector(30, 10));

            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
            Assert.Equal(target, canvas.Selection);
            Assert.Equal(target.FeatureIndex, window.ViewModel.SelectedFeatureIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AReleaseOutsideTheCanvas_AppliesTheMoveOnce()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit target, Point at) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            var outside = new Point(canvas.Bounds.Width + 20, at.Y);
            Assert.True(window.Bounds.Width > canvas.TranslatePoint(outside, window)!.Value.X);
            StartDragAndHold(window, canvas, at, new Point(canvas.Bounds.Width - 5, at.Y));
            window.MouseMove(canvas.TranslatePoint(outside, window)!.Value, RawInputModifiers.LeftMouseButton);
            MainWindowHost.Pump();

            Release(window, canvas, outside);

            Assert.NotSame(opened, window.ViewModel.Document);
            Assert.NotEqual(
                opened.Features[target.FeatureIndex].Coordinates[0],
                window.ViewModel.Document!.Features[target.FeatureIndex].Coordinates[0]
            );
            Assert.Equal("Undo Move feature", window.ViewModel.UndoDescription);
            window.ViewModel.UndoCommand.Execute(null);
            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CaptureLostWithoutARelease_AppliesNothing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, Point at) = ParkingUnderTheCursor(window, canvas);
            AirportMapDocument opened = window.ViewModel.Document!;
            IPointer? pointer = null;
            canvas.AddHandler(InputElement.PointerPressedEvent, (_, e) => pointer = e.Pointer, RoutingStrategies.Tunnel, handledEventsToo: true);
            StartDragAndHold(window, canvas, at, at + new Vector(30, 10));
            Assert.Same(canvas, pointer!.Captured);

            pointer.Capture(null);
            MainWindowHost.Pump();
            Release(window, canvas, at + new Vector(30, 10));

            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRightClickMenu_OnAnUnselectedLineNearItsVertex_OffersThatVertexsItems()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            Point at = BeyondATaxiwaysFirstVertex(window, canvas);
            Assert.Null(canvas.Selection);

            Press(window, canvas, at, MouseButton.Right);

            List<string> expected =
            [
                "Zoom to feature",
                "Copy coordinates",
                "-",
                "Fit to airport",
                "-",
                "Delete Vertex",
                "Split Taxiway Here",
                "Delete",
            ];
            Assert.Equal(expected, Labels(canvas.ActiveContextMenu!));
            Assert.False(Item(canvas.ActiveContextMenu!, "Split Taxiway Here").IsEnabled);
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheRightClickMenu_OnARunway_HasNoSplit()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (_, _, Point at) = LineSegmentUnderTheCursor(window, canvas, AirportEditorFeatureKind.Runway);

            Press(window, canvas, at, MouseButton.Right);

            Assert.Equal(
                ["Zoom to feature", "Copy coordinates", "-", "Fit to airport", "-", "Insert Vertex Here", "Delete"],
                Labels(canvas.ActiveContextMenu!)
            );
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DeleteVertex_IsDisabled_OnATwoVertexLine()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            (AirportEditorHit first, Point at) = TaxiwayVertexWhere(window, canvas, (s, k) => (s.Positions.Length > 2) && (k == 0));
            while (window.ViewModel.Document!.Features[first.FeatureIndex].Coordinates.Length > 2)
            {
                window.ViewModel.DeleteVertex(first.FeatureIndex, 1);
            }

            window.ViewModel.SelectedFeatureIndex = first.FeatureIndex;
            window.ViewModel.SelectedVertexIndex = 0;
            MainWindowHost.Pump();
            Press(window, canvas, at, MouseButton.Right);

            Assert.Equal(first, canvas.Selection);
            Assert.False(Item(canvas.ActiveContextMenu!, "Delete Vertex").IsEnabled);
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();
        }
        finally
        {
            window.Close();
        }
    }

    // On a view centred on a taxiway's first vertex at zoom 20, a pixel 2 to 5 px past that vertex, away from its second,
    // that hits the taxiway with nothing selected and that vertex with the taxiway selected.
    private static Point BeyondATaxiwaysFirstVertex(AirportEditorWindow window, AirportEditorCanvas canvas)
    {
        var scene = AirportEditorScene.Build(window.ViewModel.Document);
        foreach (AirportEditorShape shape in scene.Shapes.Where(s => s.Kind == AirportEditorFeatureKind.Taxiway))
        {
            canvas.Viewport.Zoom = 20;
            canvas.Viewport.CenterLat = shape.Positions[0].Lat;
            canvas.Viewport.CenterLon = shape.Positions[0].Lon;
            (float ax, float ay) = canvas.Viewport.LatLonToScreen(shape.Positions[0].Lat, shape.Positions[0].Lon);
            (float bx, float by) = canvas.Viewport.LatLonToScreen(shape.Positions[1].Lat, shape.Positions[1].Lon);
            double length = Math.Sqrt(((ax - bx) * (ax - bx)) + ((ay - by) * (ay - by)));
            if (length < 20)
            {
                continue;
            }

            for (int d = 2; d <= 5; d++)
            {
                Point at = new(Math.Round(ax + (d * (ax - bx) / length)), Math.Round(ay + (d * (ay - by) / length)));
                if (IsJustPastTheFirstVertex(scene, canvas, shape, at))
                {
                    return at;
                }
            }
        }

        throw new InvalidOperationException("No pixel just past a taxiway's first vertex");
    }

    private static bool IsJustPastTheFirstVertex(AirportEditorScene scene, AirportEditorCanvas canvas, AirportEditorShape shape, Point at) =>
        (AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, null) == new AirportEditorHit(shape.FeatureIndex, null))
        && (
            AirportEditorHitTest.Find(scene, canvas.Viewport, (float)at.X, (float)at.Y, shape.FeatureIndex)
            == new AirportEditorHit(shape.FeatureIndex, 0)
        );

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

    // The vNAS answer is pending: no tiles; None: the tiles show with the credit; the next map's pending answer turns
    // the cache off (its request in flight is cancelled); its late image replaces the tiles.
    [AvaloniaFact]
    public async Task ALateImage_ReplacesTheTiles_AndTheCacheIsTurnedOff()
    {
        var source = new HeldImagerySource();
        var tiles = new TileStub(serveFirst: 1);
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, source, tiles);
        try
        {
            window.Width = 1100;
            window.Height = 700;
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            Assert.Empty(canvas.SnapshotForTesting()!.Tiles);
            Assert.Equal(0, tiles.Requests);

            source.Answers[0].SetResult(null);
            await window.ViewModel.ImageryLoad;
            MainWindowHost.Pump();
            Assert.Empty(canvas.SnapshotForTesting()!.Tiles);
            CancellationToken inFlight = await tiles.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
            await tiles.Arrived.Task.WaitAsync(TimeSpan.FromSeconds(30));
            MainWindowHost.Pump();
            AirportEditorRenderSnapshot withTiles = canvas.SnapshotForTesting()!;
            Assert.NotEmpty(withTiles.Tiles);
            Assert.True(withTiles.ShowCredit);

            await ShowAndOpenOakAsync(window);
            Assert.True(inFlight.IsCancellationRequested);
            Assert.Empty(canvas.SnapshotForTesting()!.Tiles);

            source.Answers[1].SetResult(NewImage());
            await window.ViewModel.ImageryLoad;
            MainWindowHost.Pump();
            AirportEditorRenderSnapshot withImage = canvas.SnapshotForTesting()!;
            Assert.NotNull(withImage.Image);
            Assert.Empty(withImage.Tiles);
            Assert.False(withImage.ShowCredit);
        }
        finally
        {
            window.Close();
            tiles.DeleteRoot();
        }
    }

    [AvaloniaFact]
    public async Task TurningImageryOff_TurnsTheCacheOff_AndClearsTheStatusLine()
    {
        var source = new HeldImagerySource();
        var tiles = new TileStub(serveFirst: 0);
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, source, tiles);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            source.Answers[0].SetResult(null);
            await window.ViewModel.ImageryLoad;
            MainWindowHost.Pump();
            canvas.SnapshotForTesting();
            CancellationToken inFlight = await tiles.Hanging.Task.WaitAsync(TimeSpan.FromSeconds(30));
            canvas.Viewport.Zoom = 0.01;
            Assert.Equal(AirportEditorImageryStatus.ZoomIn, canvas.SnapshotForTesting()!.StatusLine);

            window.ViewModel.ShowImagery = false;

            Assert.True(inFlight.IsCancellationRequested);
            Assert.Null(canvas.SnapshotForTesting()!.StatusLine);
        }
        finally
        {
            window.Close();
            tiles.DeleteRoot();
        }
    }

    [AvaloniaFact]
    public async Task Closing_DisposesTheCanvas_BeforeTheViewModelCancelsItsRequest()
    {
        var source = new HeldImagerySource();
        var tiles = new TileStub(serveFirst: 0);
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, source, tiles);
        AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
        bool? canvasDisposedAtCancel = null;
        try
        {
            await ShowAndOpenOakAsync(window);
            using CancellationTokenRegistration registration = source.Tokens[0].Register(() => canvasDisposedAtCancel = canvas.IsDisposed);

            window.Close();
            MainWindowHost.Pump();

            Assert.True(canvasDisposedAtCancel);
        }
        finally
        {
            window.Close();
            tiles.DeleteRoot();
        }
    }

    // The download's progress is the canvas's status line until the answer; a report that comes after it shows nothing.
    [AvaloniaFact]
    public async Task DownloadProgress_IsTheStatusLine_AndALateReportShowsNothing()
    {
        var source = new HeldImagerySource();
        var tiles = new TileStub(serveFirst: 0);
        using var downloader = new AirportLayoutDownloader();
        var window = new AirportEditorWindow(
            new UserPreferences(),
            _ => new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new OakPrompts(), source, null)
            {
                ProgressPost = action => action(),
            },
            tiles.CreateCache
        );
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = window.FindControl<AirportEditorCanvas>("MapCanvas")!;
            IProgress<TowerCabDownloadProgress> progress = source.Progress[0]!;

            progress.Report(new TowerCabDownloadProgress(40, 100));
            Assert.Equal("Downloading vNAS imagery: 40%", canvas.SnapshotForTesting()!.StatusLine);

            source.Answers[0].SetResult(NewImage());
            await window.ViewModel.ImageryLoad;
            MainWindowHost.Pump();
            progress.Report(new TowerCabDownloadProgress(90, 100));

            Assert.Null(window.ViewModel.ImageryProgressText);
            Assert.Null(canvas.SnapshotForTesting()!.StatusLine);
        }
        finally
        {
            window.Close();
            tiles.DeleteRoot();
        }
    }

    private static TowerCabImage NewImage()
    {
        using var bitmap = new SKBitmap(4, 4);
        bitmap.Erase(SKColors.Red);
        return new TowerCabImage(SKImage.FromBitmap(bitmap), 37.70, -122.25, 37.74, -122.19);
    }

    private static AirportEditorWindow NewWindow(AirportLayoutDownloader downloader) =>
        NewWindow(downloader, NoAirportImagery.Instance, new TileStub(serveFirst: 0));

    private static AirportEditorWindow NewWindow(AirportLayoutDownloader downloader, IAirportImagerySource imagery, TileStub tiles) =>
        new(
            new UserPreferences(),
            _ => new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new OakPrompts(), imagery, null),
            tiles.CreateCache
        );

    // Holds every vNAS request until the test answers it.
    private sealed class HeldImagerySource : IAirportImagerySource
    {
        public List<TaskCompletionSource<TowerCabImage?>> Answers { get; } = [];

        public List<CancellationToken> Tokens { get; } = [];

        public List<IProgress<TowerCabDownloadProgress>?> Progress { get; } = [];

        public Task<TowerCabImage?> GetAsync(string artccId, string airportId, IProgress<TowerCabDownloadProgress>? progress, CancellationToken ct)
        {
            var answer = new TaskCompletionSource<TowerCabImage?>(TaskCreationOptions.RunContinuationsAsynchronously);
            Answers.Add(answer);
            Tokens.Add(ct);
            Progress.Add(progress);
            return answer.Task;
        }
    }

    // USGS for the tests: answers the first requests with a two-colour tile and holds every later one until it is
    // cancelled; the cache's disk root is a fresh temp folder.
    private sealed class TileStub(int serveFirst) : HttpMessageHandler
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "yaat-tiles-" + Guid.NewGuid().ToString("N"));
        private int _requests;

        public int Requests => Volatile.Read(ref _requests);

        public TaskCompletionSource<CancellationToken> Hanging { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Arrived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public UsgsImageryTileCache CreateCache(Action onTileArrived) =>
            new(
                this,
                _root,
                TimeProvider.System,
                action => Avalonia.Threading.Dispatcher.UIThread.Post(action),
                () =>
                {
                    onTileArrived();
                    Arrived.TrySetResult();
                }
            );

        public void DeleteRoot()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) <= serveFirst)
            {
                return TileResponse();
            }

            Hanging.TrySetResult(cancellationToken);
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new InvalidOperationException("unreachable");
        }

        private static HttpResponseMessage TileResponse()
        {
            using var bitmap = new SKBitmap(256, 256);
            bitmap.Erase(SKColors.DarkGreen);
            bitmap.SetPixel(0, 0, SKColors.White);
            using SKData png = bitmap.Encode(SKEncodedImageFormat.Png, 100);
            var content = new ByteArrayContent(png.ToArray());
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/png");
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = content };
        }
    }

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
