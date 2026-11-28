using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Services;
using Yaat.Client.ViewModels.Authoring;
using Yaat.Client.Views.Authoring;
using Yaat.Sim;
using Yaat.Sim.Authoring.Routes;
using Yaat.Sim.Commands;
using Yaat.Sim.Data;
using Yaat.Sim.Testing;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The route composer window over the committed navdata around SFO and two small video maps: it opens titled for its ARTCC
/// with a check box per map, all on; a click 3 px from SFO's symbol adds SFO and a click 40 px from every fix adds an FRD;
/// the middle button pans while a left drag moves a point; a right click on a point offers Delete point and Insert after,
/// and the Delete key removes the selected point; Save lists the route.
/// </summary>
public sealed class RouteComposerWindowTests : IDisposable
{
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(60);

    private readonly string _root = YaatPaths.Combine("route-composer-window-tests", Guid.NewGuid().ToString("N"));

    private static NavigationDatabase NavDb => NavigationDatabase.Instance;

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [AvaloniaFact]
    public async Task Opens_TitledForItsArtcc_WithACheckedBoxPerMap()
    {
        RouteComposerWindow window = await Open();
        try
        {
            Assert.Equal("Route composer — ZOA", window.Title);
            Assert.Equal("RouteComposer", RouteComposerWindow.GeometryKey);
            Assert.Equal(RouteComposerViewModel.ReadyText, window.FindControl<TextBlock>("StatusText")!.Text);
            Button maps = window.FindControl<Button>("MapsButton")!;
            Assert.Equal("Maps ▾", maps.Content);

            maps.Flyout!.ShowAt(maps);
            Dispatcher.UIThread.RunJobs();
            CheckBox[] boxes = [.. window.FindControl<ItemsControl>("MapList")!.GetLogicalDescendants().OfType<CheckBox>()];

            Assert.Equal(["East", "West"], boxes.Select(b => b.Content));
            Assert.All(boxes, b => Assert.True(b.IsChecked));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AClickNearSfosSymbol_AddsSfo_AndAClickFarFromEveryFix_AddsAnFrd()
    {
        RouteComposerWindow window = await Open();
        try
        {
            RouteComposerCanvas canvas = Canvas(window);
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(FixPosition("SFO").Lat, FixPosition("SFO").Lon);
            Click(window, canvas, new Point(sx + 3, sy), MouseButton.Left);

            Assert.Equal(RoutePoint.AtFix("SFO"), Assert.Single(window.ViewModel.Points).Point);

            Point open = FarFromEveryFix(canvas, 40);
            Click(window, canvas, open, MouseButton.Left);

            Assert.Equal(2, window.ViewModel.Points.Count);
            RouteComposerPoint frd = window.ViewModel.Points[1];
            Assert.Equal(RoutePointKind.Frd, frd.Point.Kind);
            Assert.Matches("^[A-Z0-9]{2,5}\\d{6}$", frd.Text);
            (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)open.X, (float)open.Y);
            double movedNm = GeoMath.DistanceNm(new LatLon(lat, lon), frd.Position!.Value);
            Assert.True((movedNm <= RoutePointResolver.DisplacementWarningNm) || (frd.Warning is not null), $"moved {movedNm:0.00} NM");
            Assert.Equal($"SFO {frd.Text}", window.FindControl<SelectableTextBlock>("NavPathText")!.Text);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AMiddleDrag_Pans_AndALeftDragOnAPoint_MovesIt()
    {
        RouteComposerWindow window = await Open();
        try
        {
            RouteComposerCanvas canvas = Canvas(window);
            Assert.True(window.ViewModel.AddAt(FixPosition("SFO"), "SFO"));
            RouteComposerPoint before = window.ViewModel.Points[0];
            Point open = FarFromEveryFix(canvas, 40);
            (double centerLat, double centerLon) = (canvas.Viewport.CenterLat, canvas.Viewport.CenterLon);

            Drag(window, canvas, open, MouseButton.Middle, RawInputModifiers.MiddleMouseButton);

            Assert.NotEqual(centerLon, canvas.Viewport.CenterLon);
            Assert.NotEqual(centerLat, canvas.Viewport.CenterLat);
            Assert.Equal([before], window.ViewModel.Points);

            (centerLat, centerLon) = (canvas.Viewport.CenterLat, canvas.Viewport.CenterLon);
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(FixPosition("SFO").Lat, FixPosition("SFO").Lon);
            Drag(window, canvas, new Point(sx, sy), MouseButton.Left, RawInputModifiers.LeftMouseButton);

            Assert.Equal((centerLat, centerLon), (canvas.Viewport.CenterLat, canvas.Viewport.CenterLon));
            RouteComposerPoint moved = Assert.Single(window.ViewModel.Points);
            Assert.NotEqual(before, moved);
            Assert.True(GeoMath.DistanceNm(before.Position!.Value, moved.Position!.Value) > 0.5);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ARightClickOnAPoint_OffersDeleteAndInsertAfter_AndTheDeleteKeyRemovesTheSelectedPoint()
    {
        RouteComposerWindow window = await Open();
        try
        {
            RouteComposerCanvas canvas = Canvas(window);
            RouteComposerViewModel vm = window.ViewModel;
            Assert.True(vm.AddAt(FixPosition("SFO"), "SFO"));
            Assert.True(vm.AddAt(FixPosition("OAK"), "OAK"));
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(FixPosition("SFO").Lat, FixPosition("SFO").Lon);

            Click(window, canvas, new Point(sx, sy), MouseButton.Right);

            ContextMenu menu = canvas.ActiveContextMenu!;
            MenuItem[] items = [.. menu.Items.OfType<MenuItem>()];
            Assert.Equal(["Delete point", "Insert after"], items.Select(i => i.Header));
            items[1].RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(0, vm.SelectedIndex);

            canvas.Focus();
            window.KeyPressQwerty(PhysicalKey.Delete, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(["OAK"], vm.Points.Select(p => p.Point.Fix));
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Save_ListsTheRouteUnderItsName()
    {
        RouteComposerWindow window = await Open();
        try
        {
            Assert.True(window.ViewModel.AddAt(FixPosition("SFO"), "SFO"));
            window.FindControl<TextBox>("NameBox")!.Text = "Short hop";
            Dispatcher.UIThread.RunJobs();

            Button save = window.FindControl<Button>("SaveButton")!;
            Assert.Equal("Short hop", window.ViewModel.Name);
            save.Command!.Execute(save.CommandParameter);
            Dispatcher.UIThread.RunJobs();

            SavedRoute saved = Assert.Single(new RouteLibrary(_root).List("ZOA").Routes);
            Assert.Equal("Short hop", saved.Name);
            Assert.Equal(1, window.FindControl<ListBox>("RouteList")!.ItemCount);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task ASnapWarning_IsShownInTheMessageLine()
    {
        Assert.True(NavDb.TryGetTerminalWaypoint("KSFO", "ADDMM", out (double Lat, double Lon) terminal));
        var db = NavigationDatabase.ForTesting(
            fixes: new Dictionary<string, (double Lat, double Lon)> { ["ADDMM"] = (terminal.Lat + 0.5, terminal.Lon) },
            cifpFilePath: TestVnasData.GetCifpPath()
        );
        RouteComposerWindow window = await Open(db, new FixResolutionContext("KSFO", null));
        try
        {
            Assert.True(window.ViewModel.AddAt(new LatLon(terminal.Lat, terminal.Lon), "ADDMM"));
            Dispatcher.UIThread.RunJobs();

            Assert.StartsWith(
                "Point 1: The sim reads ADDMM as another fix of that name, 30.0 NM away",
                window.FindControl<TextBlock>("MessageText")!.Text
            );
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task CtrlZOnTheCanvas_UndoesADrag()
    {
        RouteComposerWindow window = await Open();
        try
        {
            RouteComposerCanvas canvas = Canvas(window);
            Assert.True(window.ViewModel.AddAt(FixPosition("SFO"), "SFO"));
            RouteComposerPoint before = window.ViewModel.Points[0];
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(FixPosition("SFO").Lat, FixPosition("SFO").Lon);
            Drag(window, canvas, new Point(sx, sy), MouseButton.Left, RawInputModifiers.LeftMouseButton);
            Assert.NotEqual(before, window.ViewModel.Points[0]);

            canvas.Focus();
            window.KeyPressQwerty(PhysicalKey.Z, RawInputModifiers.Control);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([before], window.ViewModel.Points);
        }
        finally
        {
            window.Close();
        }
    }

    private static RouteComposerCanvas Canvas(RouteComposerWindow window) => window.FindControl<RouteComposerCanvas>("MapCanvas")!;

    // A canvas point at least the given pixels from every drawn fix, on a grid across the canvas.
    private static Point FarFromEveryFix(RouteComposerCanvas canvas, double pixels)
    {
        RouteComposerLayout layout = canvas.SnapshotForTesting()!.Scene.Layout(canvas.Viewport);
        Assert.NotEmpty(layout.Fixes);
        for (double y = 60; y < canvas.Bounds.Height - 60; y += 7)
        {
            for (double x = 60; x < canvas.Bounds.Width - 60; x += 7)
            {
                SKPoint at = new((float)x, (float)y);
                if (layout.Fixes.All(f => SKPoint.Distance(f.At, at) >= pixels) && layout.Points.All(p => SKPoint.Distance(p.At, at) >= pixels))
                {
                    return new Point(x, y);
                }
            }
        }

        throw new InvalidOperationException($"No point {pixels} px from every fix");
    }

    private static LatLon FixPosition(string name)
    {
        (double Lat, double Lon) pos = NavDb.GetFixPosition(name) ?? throw new InvalidOperationException(name + " is not in navdata");
        return new LatLon(pos.Lat, pos.Lon);
    }

    private static VideoMapData Map(string id, double lon) =>
        new() { MapId = id, Lines = [new VideoMapLine { Points = [(37.5, lon), (37.8, lon)] }] };

    private static void Click(Window window, RouteComposerCanvas canvas, Point at, MouseButton button)
    {
        Point inWindow = canvas.TranslatePoint(at, window)!.Value;
        window.MouseMove(inWindow);
        window.MouseDown(inWindow, button);
        window.MouseUp(inWindow, button);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Drag(Window window, RouteComposerCanvas canvas, Point start, MouseButton button, RawInputModifiers held)
    {
        Point from = canvas.TranslatePoint(start, window)!.Value;
        Point to = from + new Vector(60, 40);
        window.MouseMove(from);
        window.MouseDown(from, button);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to, held);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, button);
        Dispatcher.UIThread.RunJobs();
    }

    private Task<RouteComposerWindow> Open() => Open(NavDb, FixResolutionContext.None);

    private async Task<RouteComposerWindow> Open(NavigationDatabase navDb, FixResolutionContext fixes)
    {
        IReadOnlyList<RouteComposerMap> maps = [new("East", Map("EAST", -122.2)), new("West", Map("WEST", -122.6))];
        var context = new RouteComposerContext("ZOA", fixes, _ => Task.FromResult(maps));
        var vm = new RouteComposerViewModel(context, new RouteLibrary(_root), () => navDb);
        var window = new RouteComposerWindow(vm, new UserPreferences());
        window.Show();
        Dispatcher.UIThread.RunJobs();
        await window.LoadTask.WaitAsync(HangGuard);
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        Canvas(window).CenterOn(FixPosition("SFO"), 20);
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return window;
    }
}
