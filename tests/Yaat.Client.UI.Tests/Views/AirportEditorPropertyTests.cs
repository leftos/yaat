using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.VisualTree;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Helpers;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim.Authoring.AirportMap;
using Yaat.Sim.Data.Airport;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The Airport Editor's property panel edits the selected feature: Enter or focus loss commits a value as one undo step,
/// Esc restores it, and keys typed in a value box stay in the box. The snap distance and target kinds come from the
/// user's preferences.
/// </summary>
public partial class AirportEditorWindowTests
{
    private const double FeetPerDegreeOfLatitude = 6076.12 * 60;

    [AvaloniaFact]
    public async Task RenamingTwy1ToA_InThePanel_WithEnter_IsOneEditNameStep_AndUndoRestoresIt()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            int added = await ShowOakAndDrawATaxiwayAsync(window);
            TextBox box = ValueBox(window, "name");
            box.Focus();
            MainWindowHost.Pump();

            box.Text = "A";
            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Equal("A", FeatureName(window.ViewModel.Document!.Features[added]));
            Assert.Equal("Undo Edit name", window.ViewModel.UndoDescription);

            window.ViewModel.UndoCommand.Execute(null);
            MainWindowHost.Pump();

            Assert.Equal("TWY1", FeatureName(window.ViewModel.Document!.Features[added]));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task EscInTheNameBox_RestoresTheValue_AndCommitsNothing_AndFocusLossCommits()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            int added = await ShowOakAndDrawATaxiwayAsync(window);
            AirportMapDocument before = window.ViewModel.Document!;
            TextBox box = ValueBox(window, "name");
            box.Focus();
            MainWindowHost.Pump();

            box.Text = "A";
            window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Equal("TWY1", box.Text);
            Assert.Same(before, window.ViewModel.Document);
            Assert.Equal(added, window.ViewModel.SelectedFeatureIndex);

            box.Text = "B";
            window.FindControl<ItemsControl>("PropertyRows")!.GetVisualDescendants().OfType<TextBox>().First().Focus();
            MainWindowHost.Pump();

            Assert.Equal("B", FeatureName(window.ViewModel.Document!.Features[added]));
            Assert.Equal("Undo Edit name", window.ViewModel.UndoDescription);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task TInTheNameBox_PicksNoMode_AndCtrlZInTheBox_LeavesTheMapAlone()
    {
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader);
        try
        {
            await ShowOakAndDrawATaxiwayAsync(window);
            AirportMapDocument drawn = window.ViewModel.Document!;
            TextBox box = ValueBox(window, "name");
            box.Focus();
            MainWindowHost.Pump();

            window.KeyPressQwerty(PhysicalKey.T, RawInputModifiers.None);
            MainWindowHost.Pump();

            Assert.Equal(AirportEditorMode.Select, window.ViewModel.Mode);

            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            MainWindowHost.Pump();

            Assert.Same(drawn, window.ViewModel.Document);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClick20FtFromATaxiwayVertex_SnapsAt25Ft_NotAtTheDefault10Ft_NorWithTaxiwayVerticesOff()
    {
        SnapOptions at25 = SnapOptions.Default with { ThresholdFt = 25 };

        (MapPosition placed, MapPosition vertex, _) = await PlaceParkingBesideATaxiwayVertexAsync(at25);
        Assert.Equal(vertex, placed);

        (placed, vertex, MapPosition click) = await PlaceParkingBesideATaxiwayVertexAsync(SnapOptions.Default);
        Assert.NotEqual(vertex, placed);
        Assert.Equal(click, placed);

        (placed, vertex, click) = await PlaceParkingBesideATaxiwayVertexAsync(at25 with { TaxiwayVertices = false });
        Assert.NotEqual(vertex, placed);
        Assert.Equal(click, placed);
    }

    [AvaloniaFact]
    public async Task AParkingDragged20FtFromATaxiwayVertex_LandsOnIt_AtA25FtSnapThreshold()
    {
        var preferences = new UserPreferences();
        SnapOptions saved = preferences.AirportEditorSnapOptions;
        preferences.SetAirportEditorSnap(SnapOptions.Default with { ThresholdFt = 25 });
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, preferences);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument document = window.ViewModel.Document!;
            (MapPosition vertex, Point at) = TaxiwayVertexWith20FtClick(canvas, document);
            int parking = document.Features.Select((f, i) => (f, i)).First(x => x.f.FeatureType == "parking").i;
            Point from = at + new Vector(40, 0);
            window.ViewModel.MoveFeature(parking, PositionAt(canvas, from));
            MainWindowHost.Pump();

            DragLeft(window, canvas, from, at);

            Assert.Equal(vertex, window.ViewModel.Document!.Features[parking].Coordinates[0]);
        }
        finally
        {
            window.Close();
            preferences.SetAirportEditorSnap(saved);
        }
    }

    [AvaloniaFact]
    public void ActivatingTheWindow_ReadsTheSnapSettingsFromThePreferences()
    {
        var preferences = new UserPreferences();
        SnapOptions saved = preferences.AirportEditorSnapOptions;
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, preferences);
        try
        {
            window.Show();
            MainWindowHost.Pump();
            SnapOptions changed = new(25, false, true, false, true, false);
            preferences.SetAirportEditorSnap(changed);

            window.Activate();
            MainWindowHost.Pump();

            Assert.Equal(changed, MapCanvasOf(window).SnapOptions);
        }
        finally
        {
            window.Close();
            preferences.SetAirportEditorSnap(saved);
        }
    }

    private static AirportEditorWindow NewWindow(AirportLayoutDownloader downloader, UserPreferences preferences) =>
        new(
            preferences,
            _ => new AirportEditorViewModel(new AirportMapSourceLoader(downloader), new OakPrompts(), NoAirportImagery.Instance, null),
            new TileStub(serveFirst: 0).CreateCache
        );

    // Opens KOAK, draws TWY1 and returns to Select with it selected; gives its index.
    private static async Task<int> ShowOakAndDrawATaxiwayAsync(AirportEditorWindow window)
    {
        await ShowAndOpenOakAsync(window);
        AirportEditorCanvas canvas = MapCanvasOf(window);
        PressKey(window, PhysicalKey.T);
        Press(window, canvas, CornerClicks[0], MouseButton.Left);
        Press(window, canvas, CornerClicks[1], MouseButton.Left);
        PressKey(window, PhysicalKey.Enter);
        PressKey(window, PhysicalKey.Escape);
        int added = window.ViewModel.SelectedFeatureIndex!.Value;
        Assert.Equal("TWY1", FeatureName(window.ViewModel.Document!.Features[added]));
        Assert.Equal(AirportEditorMode.Select, window.ViewModel.Mode);
        return added;
    }

    private static TextBox ValueBox(AirportEditorWindow window, string key) =>
        window
            .FindControl<ItemsControl>("PropertyRows")!
            .GetVisualDescendants()
            .OfType<TextBox>()
            .Single(b => (!b.IsReadOnly) && (b.DataContext is AirportEditorPropertyRow row) && (row.Name == key));

    // With the preferences holding the options, places a parking with a click 20 ft north of a KOAK taxiway vertex that a
    // 25 ft taxiway snap reaches and no other target does; gives where the parking landed, the vertex and the click.
    private static async Task<(MapPosition Placed, MapPosition Vertex, MapPosition Click)> PlaceParkingBesideATaxiwayVertexAsync(SnapOptions options)
    {
        var preferences = new UserPreferences();
        SnapOptions saved = preferences.AirportEditorSnapOptions;
        preferences.SetAirportEditorSnap(options);
        using var downloader = new AirportLayoutDownloader();
        AirportEditorWindow window = NewWindow(downloader, preferences);
        try
        {
            await ShowAndOpenOakAsync(window);
            AirportEditorCanvas canvas = MapCanvasOf(window);
            AirportMapDocument document = window.ViewModel.Document!;
            (MapPosition vertex, Point at) = TaxiwayVertexWith20FtClick(canvas, document);
            int count = document.Features.Count;
            PressKey(window, PhysicalKey.P);

            Press(window, canvas, at, MouseButton.Left);

            return (window.ViewModel.Document!.Features[count].Coordinates[0], vertex, PositionAt(canvas, at));
        }
        finally
        {
            window.Close();
            preferences.SetAirportEditorSnap(saved);
        }
    }

    private static (MapPosition Vertex, Point At) TaxiwayVertexWith20FtClick(AirportEditorCanvas canvas, AirportMapDocument document)
    {
        SnapOptions at25 = SnapOptions.Default with { ThresholdFt = 25 };
        foreach (MapPosition vertex in document.Features.Where(f => f.FeatureType == "taxiway").SelectMany(f => f.Coordinates))
        {
            Point at = ZoomOn(canvas, vertex with { Lat = vertex.Lat + (20 / FeetPerDegreeOfLatitude) });
            MapPosition click = PositionAt(canvas, at);
            if (
                (SnapEngine.Find(document, click, at25, null, null)?.Position == vertex)
                && (SnapEngine.Find(document, click, SnapOptions.Default, null, null) is null)
                && (SnapEngine.Find(document, click, at25 with { TaxiwayVertices = false }, null, null) is null)
            )
            {
                return (vertex, at);
            }
        }

        throw new InvalidOperationException("No KOAK taxiway vertex has a 20 ft click only it snaps to");
    }
}
