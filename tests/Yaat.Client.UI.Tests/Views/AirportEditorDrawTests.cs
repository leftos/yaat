using System.Collections.Immutable;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Xunit;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

// The draw and place modes on the committed KOAK map.
public partial class AirportEditorWindowTests
{
    // Inside the empty margin the fit leaves around the map, more than the double-click distance apart.
    private static readonly Point[] CornerClicks = [new(6, 6), new(26, 6), new(26, 26)];

    [AvaloniaFact]
    public async Task DrawTaxiway_ThreeClicksThenADoubleClickOnTheThird_AddsAThreeVertexTaxiway()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            int count = window.ViewModel.Document!.Features.Count;
            PressKey(window, PhysicalKey.T);
            foreach (Point at in CornerClicks)
            {
                Press(window, canvas, at, MouseButton.Left);
            }

            Press(window, canvas, CornerClicks[2], MouseButton.Left);

            AirportMapDocument document = window.ViewModel.Document!;
            Assert.Equal(count + 1, document.Features.Count);
            MapFeature taxiway = document.Features[count];
            Assert.Equal("taxiway", taxiway.FeatureType);
            Assert.Equal("TWY1", FeatureName(taxiway));
            AssertAt(canvas, CornerClicks, taxiway.Coordinates);
            Assert.Empty(canvas.DrawingVertices);
            Assert.Equal(count, window.ViewModel.SelectedFeatureIndex);
            Assert.Equal(AirportEditorMode.DrawTaxiway, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DrawTaxiway_EnterFinishesTwoClicks_ButNotOne()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            int count = window.ViewModel.Document!.Features.Count;
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            PressKey(window, PhysicalKey.Enter);

            Assert.Equal(count + 1, window.ViewModel.Document!.Features.Count);
            AssertAt(canvas, CornerClicks[..2], window.ViewModel.Document.Features[count].Coordinates);
            Assert.Equal("Undo Draw taxiway", window.ViewModel.UndoDescription);

            Press(window, canvas, CornerClicks[2], MouseButton.Left);
            PressKey(window, PhysicalKey.Enter);

            Assert.Equal(count + 1, window.ViewModel.Document!.Features.Count);
            Assert.Single(canvas.DrawingVertices);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EscMidDrawing_CancelsItAndKeepsTheMode_AndEscAgainReturnsToSelect()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument opened = window.ViewModel.Document!;
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            PressKey(window, PhysicalKey.Escape);

            Assert.Empty(canvas.DrawingVertices);
            Assert.Same(opened, window.ViewModel.Document);
            Assert.Equal(AirportEditorMode.DrawTaxiway, window.ViewModel.Mode);

            PressKey(window, PhysicalKey.Escape);

            Assert.Equal(AirportEditorMode.Select, window.ViewModel.Mode);
            Assert.Equal("Select", canvas.SnapshotForTesting()!.ModeText);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task DrawRunway_TheSecondClickFinishesATwoVertexRunwayNamedRwy1()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            int count = window.ViewModel.Document!.Features.Count;
            PressKey(window, PhysicalKey.R);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Assert.Equal(count, window.ViewModel.Document!.Features.Count);

            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            MapFeature runway = window.ViewModel.Document!.Features[count];
            Assert.Equal("runway", runway.FeatureType);
            Assert.Equal("RWY1", FeatureName(runway));
            AssertAt(canvas, CornerClicks[..2], runway.Coordinates);
            Assert.Empty(canvas.DrawingVertices);
            Assert.Equal("Undo Draw runway", window.ViewModel.UndoDescription);
            Assert.Equal(AirportEditorMode.DrawRunway, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    // KOAK already has HELI1 and HELI2, so the lowest unused helipad number is 3.
    [AvaloniaFact]
    public async Task PlaceModes_OneClickAddsThePoint_WithItsAutoName()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            (PhysicalKey Key, string Type, string Name, string Undo)[] modes =
            [
                (PhysicalKey.P, "parking", "PARK1", "Undo Place parking"),
                (PhysicalKey.S, "spot", "SPOT1", "Undo Place spot"),
                (PhysicalKey.H, "helipad", "HELI3", "Undo Place helipad"),
            ];
            for (int i = 0; i < modes.Length; i++)
            {
                int count = window.ViewModel.Document!.Features.Count;
                PressKey(window, modes[i].Key);

                Press(window, canvas, CornerClicks[i], MouseButton.Left);

                Assert.Equal(count + 1, window.ViewModel.Document!.Features.Count);
                MapFeature point = window.ViewModel.Document.Features[count];
                Assert.Equal(modes[i].Type, point.FeatureType);
                Assert.Equal("Point", point.GeometryType);
                Assert.Equal(modes[i].Name, FeatureName(point));
                AssertAt(canvas, [CornerClicks[i]], point.Coordinates);
                Assert.Equal(modes[i].Undo, window.ViewModel.UndoDescription);
            }
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClickNearAMapVertex_PutsTheNewVertexExactlyOnIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument document = window.ViewModel.Document!;
            int count = document.Features.Count;
            (MapPosition target, Point centre, Point near) = TaxiwayVertexSnappedToFromNearby(canvas, document);
            PressKey(window, PhysicalKey.T);

            Press(window, canvas, near, MouseButton.Left);
            Press(window, canvas, centre + new Vector(60, 0), MouseButton.Left);
            PressKey(window, PhysicalKey.Enter);

            Assert.Equal(target, window.ViewModel.Document!.Features[count].Coordinates[0]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClickNearAnUnsavedVertex_SnapsToIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument document = window.ViewModel.Document!;
            int count = document.Features.Count;
            double north = document.Features.SelectMany(f => f.Coordinates).Max(p => p.Lat);
            Point first = ZoomOn(canvas, new MapPosition(document.Features.SelectMany(f => f.Coordinates).First().Lon, north + 0.01));
            Point second = first + new Vector(60, 0);
            Point near = first + new Vector(3, 0);
            Assert.Null(SnapEngine.Find(document, PositionAt(canvas, near), SnapOptions.Default, null, null));
            PressKey(window, PhysicalKey.T);

            Press(window, canvas, first, MouseButton.Left);
            Press(window, canvas, second, MouseButton.Left);
            Press(window, canvas, near, MouseButton.Left);
            PressKey(window, PhysicalKey.Enter);

            ImmutableArray<MapPosition> drawn = window.ViewModel.Document!.Features[count].Coordinates;
            Assert.Equal(3, drawn.Length);
            Assert.Equal(drawn[0], drawn[2]);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARightClickWhileDrawing_OpensTheMenu_AndTheDrawingSurvives()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            int count = window.ViewModel.Document!.Features.Count;
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            Press(window, canvas, CornerClicks[2], MouseButton.Right);

            Assert.NotNull(canvas.ActiveContextMenu);
            Assert.Equal(2, canvas.DrawingVertices.Length);
            canvas.ActiveContextMenu!.Close();
            MainWindowHost.Pump();
            PressKey(window, PhysicalKey.Enter);
            AssertAt(canvas, CornerClicks[..2], window.ViewModel.Document!.Features[count].Coordinates);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AddParkingHere_OnEmptySpace_PlacesPark1AtTheClick()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument document = window.ViewModel.Document!;
            int count = document.Features.Count;
            Assert.Null(SnapEngine.Find(document, PositionAt(canvas, EmptyCorner), SnapOptions.Default, null, null));
            Press(window, canvas, EmptyCorner, MouseButton.Right);

            ClickItem(canvas.ActiveContextMenu!, "Add Parking Here");

            MapFeature parking = window.ViewModel.Document!.Features[count];
            Assert.Equal("parking", parking.FeatureType);
            Assert.Equal("PARK1", FeatureName(parking));
            AssertAt(canvas, [EmptyCorner], parking.Coordinates);
            Assert.Equal("Undo Place parking", window.ViewModel.UndoDescription);
            Assert.Equal(count, window.ViewModel.SelectedFeatureIndex);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TheModeKeys_SwitchTheModeAndTheHudText_ButNotWithCtrlHeld()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            (PhysicalKey Key, AirportEditorMode Mode, string Text, string Button)[] keys =
            [
                (PhysicalKey.T, AirportEditorMode.DrawTaxiway, "Draw taxiway", "TaxiwayModeButton"),
                (PhysicalKey.R, AirportEditorMode.DrawRunway, "Draw runway", "RunwayModeButton"),
                (PhysicalKey.P, AirportEditorMode.PlaceParking, "Place parking", "ParkingModeButton"),
                (PhysicalKey.S, AirportEditorMode.PlaceSpot, "Place spot", "SpotModeButton"),
                (PhysicalKey.H, AirportEditorMode.PlaceHelipad, "Place helipad", "HelipadModeButton"),
            ];
            Assert.True(window.FindControl<ToggleButton>("SelectModeButton")!.IsChecked);
            foreach ((PhysicalKey key, AirportEditorMode mode, string text, string button) in keys)
            {
                PressKey(window, key);

                Assert.Equal(mode, window.ViewModel.Mode);
                Assert.Equal(text, canvas.SnapshotForTesting()!.ModeText);
                Assert.True(window.FindControl<ToggleButton>(button)!.IsChecked);
                Assert.False(window.FindControl<ToggleButton>("SelectModeButton")!.IsChecked);
            }

            window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Equal(AirportEditorMode.PlaceHelipad, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARenderWhileDrawing_CarriesTheGhostToTheCursor()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            window.MouseMove(canvas.TranslatePoint(CornerClicks[2], window)!.Value);
            MainWindowHost.Pump();

            AirportEditorRenderSnapshot frame = canvas.SnapshotForTesting()!;
            Assert.NotNull(frame.Ghost);
            AssertAt(canvas, CornerClicks[..2], frame.Ghost.Vertices);
            AssertAt(canvas, [CornerClicks[2]], [frame.Ghost.Cursor!.Value]);
            Assert.Null(frame.SnapAt);
            using SkiaSharp.SKBitmap rendered = canvas.RenderForTesting();
            SkiaSharp.SKColor onTheDrawnSegment = rendered.GetPixel(16, 6);
            Assert.True((onTheDrawnSegment.Red > 120) && (onTheDrawnSegment.Green > 120) && (onTheDrawnSegment.Blue > 120));

            PressKey(window, PhysicalKey.Escape);

            Assert.Null(canvas.SnapshotForTesting()!.Ghost);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Enter_FinishesADrawing_AfterAClickOnAToolbarToggle()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            int count = window.ViewModel.Document!.Features.Count;
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);
            ClickControl(window, window.FindControl<ToggleButton>("ImageryToggle")!);

            PressKey(window, PhysicalKey.Enter);

            Assert.Equal(count + 1, window.ViewModel.Document!.Features.Count);
            MapFeature taxiway = window.ViewModel.Document.Features[count];
            Assert.Equal("TWY1", FeatureName(taxiway));
            AssertAt(canvas, CornerClicks[..2], taxiway.Coordinates);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PickingAModeMidDrag_CancelsTheDrag_AndTheReleaseMovesNothing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            (AirportEditorHit vertex, Point at) = TaxiwayVertexUnderTheCursor(window, canvas);
            Press(window, canvas, at, MouseButton.Left);
            AirportMapDocument opened = window.ViewModel.Document!;
            MapPosition before = opened.Features[vertex.FeatureIndex].Coordinates[vertex.VertexIndex!.Value];
            StartDragAndHold(window, canvas, at, at + new Vector(25, 15));

            PressKey(window, PhysicalKey.T);
            Release(window, canvas, at + new Vector(25, 15));

            Assert.Same(opened, window.ViewModel.Document);
            Assert.False(window.ViewModel.UndoCommand.CanExecute(null));
            Assert.Equal(before, window.ViewModel.Document!.Features[vertex.FeatureIndex].Coordinates[vertex.VertexIndex.Value]);
            Assert.Equal(AirportEditorMode.DrawTaxiway, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task APanWhileDrawing_KeepsTheGhostEndUnderThePointer()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);
            Point from = canvas.TranslatePoint(new Point(120, 100), window)!.Value;
            Point to = canvas.TranslatePoint(new Point(170, 140), window)!.Value;
            window.MouseMove(from);
            MainWindowHost.Pump();
            window.MouseDown(from, MouseButton.Middle);
            MainWindowHost.Pump();

            window.MouseMove(new Point((from.X + to.X) / 2, from.Y), RawInputModifiers.MiddleMouseButton);
            MainWindowHost.Pump();
            window.MouseMove(to, RawInputModifiers.MiddleMouseButton);
            MainWindowHost.Pump();

            AirportEditorRenderSnapshot frame = canvas.SnapshotForTesting()!;
            AssertAt(canvas, [new Point(170, 140)], [frame.Ghost!.Cursor!.Value]);
            window.MouseUp(to, MouseButton.Middle);
            MainWindowHost.Pump();
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task PickingTheSameModeAgain_DropsTheDrawing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            PressKey(window, PhysicalKey.T);

            Assert.Empty(canvas.DrawingVertices);
            Assert.Equal(AirportEditorMode.DrawTaxiway, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ClickingTheTaxiwayButtonTwice_KeepsItChecked_InDrawTaxiway()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            ToggleButton taxiway = window.FindControl<ToggleButton>("TaxiwayModeButton")!;

            ClickControl(window, taxiway);
            ClickControl(window, taxiway);

            Assert.True(taxiway.IsChecked);
            Assert.False(window.FindControl<ToggleButton>("SelectModeButton")!.IsChecked);
            Assert.Equal(AirportEditorMode.DrawTaxiway, window.ViewModel.Mode);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlZ_UndoesTheLastFeature_AndKeepsTheDrawingInProgress()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument opened = window.ViewModel.Document!;
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);
            PressKey(window, PhysicalKey.Enter);
            Press(window, canvas, CornerClicks[2], MouseButton.Left);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Same(opened, window.ViewModel.Document);
            AssertAt(canvas, [CornerClicks[2], CornerClicks[0]], canvas.DrawingVertices);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AnOpenFromDrawTaxiway_ReturnsToSelect_AndDropsTheDrawing()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            PressKey(window, PhysicalKey.T);
            Press(window, canvas, CornerClicks[0], MouseButton.Left);
            Press(window, canvas, CornerClicks[1], MouseButton.Left);

            await window.ViewModel.OpenFileCommand.ExecuteAsync(null);
            MainWindowHost.Pump();

            Assert.Equal(AirportEditorMode.Select, window.ViewModel.Mode);
            Assert.Empty(canvas.DrawingVertices);
            Assert.True(window.FindControl<ToggleButton>("SelectModeButton")!.IsChecked);
            Assert.False(window.FindControl<ToggleButton>("TaxiwayModeButton")!.IsChecked);
        }
        finally
        {
            window.Close();
        }
    }

    private static void ClickControl(Window window, Control control)
    {
        Point centre = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)!.Value;
        window.MouseMove(centre);
        MainWindowHost.Pump();
        window.MouseDown(centre, MouseButton.Left);
        MainWindowHost.Pump();
        window.MouseUp(centre, MouseButton.Left);
        MainWindowHost.Pump();
    }

    private static AirportEditorCanvas MapCanvasOf(AirportEditorWindow window) => window.FindControl<AirportEditorCanvas>("MapCanvas")!;

    private static void PressKey(AirportEditorWindow window, PhysicalKey key)
    {
        window.KeyPressQwerty(key, RawInputModifiers.None);
        MainWindowHost.Pump();
    }

    private static MapPosition PositionAt(AirportEditorCanvas canvas, Point at)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)at.X, (float)at.Y);
        return new MapPosition(lon, lat);
    }

    private static void AssertAt(AirportEditorCanvas canvas, Point[] clicks, ImmutableArray<MapPosition> positions)
    {
        Assert.Equal(clicks.Length, positions.Length);
        for (int i = 0; i < clicks.Length; i++)
        {
            MapPosition expected = PositionAt(canvas, clicks[i]);
            Assert.Equal(expected.Lat, positions[i].Lat, 1e-9);
            Assert.Equal(expected.Lon, positions[i].Lon, 1e-9);
        }
    }

    // Centres the view on a position at a zoom where the 10 ft snap threshold is a few pixels, and gives its canvas point.
    private static Point ZoomOn(AirportEditorCanvas canvas, MapPosition position)
    {
        canvas.Viewport.CenterLat = position.Lat;
        canvas.Viewport.CenterLon = position.Lon;
        canvas.Viewport.Zoom = 200;
        (float x, float y) = canvas.Viewport.LatLonToScreen(position.Lat, position.Lon);
        return new Point(x, y);
    }

    // A taxiway vertex a click 2 px off snaps to (some lie a fraction of a foot from another target), with the view centred
    // on it, and that click.
    private static (MapPosition Target, Point Centre, Point Near) TaxiwayVertexSnappedToFromNearby(
        AirportEditorCanvas canvas,
        AirportMapDocument document
    )
    {
        foreach (MapPosition vertex in document.Features.Where(f => f.FeatureType == "taxiway").SelectMany(f => f.Coordinates))
        {
            Point centre = ZoomOn(canvas, vertex);
            Point near = centre + new Vector(2, 1);
            if (SnapEngine.Find(document, PositionAt(canvas, near), SnapOptions.Default, null, null)?.Position == vertex)
            {
                return (vertex, centre, near);
            }
        }

        throw new InvalidOperationException("No KOAK taxiway vertex is the snap of a click beside it");
    }

    private static string? FeatureName(MapFeature feature) => feature.CloneProperties()?["name"]?.GetValue<string>();
}
