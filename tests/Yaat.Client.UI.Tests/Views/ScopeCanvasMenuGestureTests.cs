using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views;
using Yaat.Client.Views.Map;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// The Scope's pointer menus: Ctrl(Cmd)+click opens an aircraft's flight plan in place of the select, a click on the empty
// map clears the selection, and a right click on stacked targets offers a picker in place of the nearest one's menu.
public class ScopeCanvasMenuGestureTests
{
    // Empty map points, far from the track the tests draw at (300, 300).
    private static readonly Point MapA = new(100, 500);
    private static readonly Point MapB = new(600, 450);
    private static readonly Point Target = new(300, 300);

    // The platform's action modifier (Cmd on macOS, Ctrl elsewhere), as PlatformHelper.HasActionModifier reads it.
    private static RawInputModifiers ActionModifier => PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;

    [AvaloniaFact]
    public void EmptyMap_LeftClick_ClearsTheSelection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            Click(window, view.Canvas, Target, MouseButton.Left, RawInputModifiers.None);
            Assert.Same(ac, main.SelectedAircraft);

            Click(window, view.Canvas, MapA, MouseButton.Left, RawInputModifiers.None);

            Assert.Null(main.SelectedAircraft);
            Assert.Null(scope.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EmptyMap_LeftClick_WithTheRingsToolArmed_KeepsTheSelection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            scope.ArmedRingTool = ScopeRingTool.JRing;
            main.SelectedAircraft = ac;

            Click(window, view.Canvas, MapA, MouseButton.Left, RawInputModifiers.None);

            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EmptyMap_AltDrag_KeepsTheSelection()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            main.SelectedAircraft = ac;
            Point start = InWindow(window, view.Canvas, MapA);
            Point end = InWindow(window, view.Canvas, MapB);

            window.MouseMove(start, RawInputModifiers.Alt);
            window.MouseDown(start, MouseButton.Left, RawInputModifiers.Alt);
            window.MouseMove(end, RawInputModifiers.Alt | RawInputModifiers.LeftMouseButton);
            window.MouseUp(end, MouseButton.Left, RawInputModifiers.Alt);
            Dispatcher.UIThread.RunJobs();

            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EmptyMap_LeftClick_WithTextInThePreview_FillsThePoint_AndKeepsTheSelection()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            main.SelectedAircraft = ac;
            scope.TypeEntryText("QQ 250");

            Click(window, view.Canvas, MapA, MouseButton.Left, RawInputModifiers.None);

            Assert.StartsWith("QQ 250 //", scope.EntryText, StringComparison.Ordinal);
            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void CtrlClick_OnATarget_OpensTheFlightPlan_AndSelectsNothing()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);

            Click(window, view.Canvas, Target, MouseButton.Left, ActionModifier);

            Assert.Equal("UAL238 - Flight Plan", FlightPlanEditorManager.OpenEditor?.Title);
            Assert.Null(main.SelectedAircraft);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void CtrlClick_OnABlockField_OpensTheFlightPlan_InsteadOfTheFieldMenu()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            FieldRect altitude = track.Block.Fields.First(f => f.Slot == ScopeFieldSlot.Altitude);

            Click(window, view.Canvas, new Point(altitude.Rect.MidX, altitude.Rect.MidY), MouseButton.Left, ActionModifier);

            Assert.Equal("UAL238 - Flight Plan", FlightPlanEditorManager.OpenEditor?.Title);
            Assert.Null(view.ActiveFieldPopup);
            Assert.Null(scope.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void CtrlShiftClick_OnATarget_OpensNoFlightPlan()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);

            Click(window, view.Canvas, Target, MouseButton.Left, ActionModifier | RawInputModifiers.Shift);

            Assert.Null(FlightPlanEditorManager.OpenEditor);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void CtrlClick_WithTextInThePreview_FillsInsteadOfOpeningTheFlightPlan()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            scope.TypeEntryText("QQ 250");

            Click(window, view.Canvas, Target, MouseButton.Left, ActionModifier);

            Assert.Null(FlightPlanEditorManager.OpenEditor);
            Assert.Contains("UAL238", scope.EntryText, StringComparison.Ordinal);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EmptyMap_RightClick_RaisesTheMapPointAtThePress()
    {
        (MainViewModel main, ScopeViewModel scope, ScopeView view, Window window) = ShowScope();
        try
        {
            var raised = new List<(LatLon Position, Point Screen)>();
            view.Canvas.MapRightClicked += (position, screen) => raised.Add((position, screen));

            Click(window, view.Canvas, MapA, MouseButton.Right, RawInputModifiers.None);

            (LatLon position, Point screen) = Assert.Single(raised);
            Assert.Equal(MapA, screen);
            Assert.Null(scope.SelectedAircraft);
            // The view opens the point menu there: with nothing selected, its point section and "Settings for this view…".
            ContextMenu menu = Assert.IsType<ContextMenu>(view.ActiveContextMenu);
            Assert.Equal("Settings for this view…", (menu.Items[^1] as MenuItem)?.Header);
            (double lat, double lon) = view.Canvas.Viewport.ScreenToLatLon((float)MapA.X, (float)MapA.Y);
            Assert.Equal(lat, position.Lat, 3);
            Assert.Equal(lon, position.Lon, 3);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void EmptyMap_RightDrag_RaisesNoMapPoint_AndStillPans()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            var raised = new List<Point>();
            view.Canvas.MapRightClicked += (_, screen) => raised.Add(screen);
            Point start = InWindow(window, view.Canvas, MapA);
            double centerLon = view.Canvas.Viewport.CenterLon;

            window.MouseDown(start, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            window.MouseMove(start + new Vector(40, 25), RawInputModifiers.RightMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(start + new Vector(40, 25), MouseButton.Right);
            Dispatcher.UIThread.RunJobs();

            Assert.Empty(raised);
            Assert.NotEqual(centerLon, view.Canvas.Viewport.CenterLon);
            Assert.Null(main.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ArmedMeasure_RightClick_OnTheEmptyMap_RaisesNoMapPoint()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            var raised = new List<Point>();
            view.Canvas.MapRightClicked += (_, screen) => raised.Add(screen);
            main.Measure.Arm();

            Click(window, view.Canvas, MapA, MouseButton.Right, RawInputModifiers.None);

            Assert.Empty(raised);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightPressWhoseReleaseNeverCame_ThenATargetPress_RaisesNoMapPoint()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            var raised = new List<Point>();
            view.Canvas.MapRightClicked += (_, screen) => raised.Add(screen);
            Point start = InWindow(window, view.Canvas, MapA);

            // A right press on the map whose release went elsewhere, then a right click on the aircraft: the stale press is
            // abandoned, so its release cannot open the map menu at the old point.
            window.MouseDown(start, MouseButton.Right);
            Dispatcher.UIThread.RunJobs();
            Click(window, view.Canvas, Target, MouseButton.Right, RawInputModifiers.None);

            Assert.Empty(raised);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClick_TwoStackedTargets_OpensThePicker_WithBothLabels()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X + 4, Target.Y);
            AddAircraft(main, view.Canvas, "SWA12", Target.X - 6, Target.Y);
            var raised = new List<string>();
            view.Canvas.AircraftRightClicked += (callsign, _) => raised.Add(callsign);
            var mapPoints = new List<Point>();
            view.Canvas.MapRightClicked += (_, screen) => mapPoints.Add(screen);
            IReadOnlyList<RightClickTarget> targets = view.Canvas.FindRightClickTargets(Target);
            Assert.Equal(2, targets.Count);
            Assert.All(targets, target => Assert.False(target.ViaDataBlock));

            Click(window, view.Canvas, Target, MouseButton.Right, RawInputModifiers.None);

            ContextMenu? picker = view.Canvas.ActiveRightClickPicker;
            Assert.NotNull(picker);
            Assert.Equal(["SWA12", "UAL238"], picker!.Items.OfType<MenuItem>().Select(HeaderCallsign).Order());
            Assert.Empty(raised);
            Assert.Empty(mapPoints);
            Assert.Null(view.ActiveContextMenu);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClick_OneTarget_RaisesTheAircraftDirectly_AndOpensItsMenu()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            var raised = new List<(string Callsign, Point Screen)>();
            view.Canvas.AircraftRightClicked += (callsign, screen) => raised.Add((callsign, screen));
            var mapPoints = new List<Point>();
            view.Canvas.MapRightClicked += (_, screen) => mapPoints.Add(screen);

            Click(window, view.Canvas, Target, MouseButton.Right, RawInputModifiers.None);

            Assert.Equal([("UAL238", Target)], raised);
            Assert.Empty(mapPoints);
            Assert.Null(view.Canvas.ActiveRightClickPicker);
            Assert.NotNull(view.ActiveContextMenu);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RightClick_ABlockOverANearSymbol_ListsTheBlockFirst_AndChoosingItRaisesItAtTheClick()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScope();
        try
        {
            AddAircraft(main, view.Canvas, "UAL238", Target.X, Target.Y);
            ScopeTrack track = Assert.Single(view.Canvas.CurrentFrame.Tracks);
            var blockPoint = new Point(track.Block.Bounds.MidX, track.Block.Bounds.MidY);
            AddAircraft(main, view.Canvas, "SWA12", blockPoint.X, blockPoint.Y);
            IReadOnlyList<RightClickTarget> targets = view.Canvas.FindRightClickTargets(blockPoint);
            Assert.Equal(2, targets.Count);
            Assert.Equal("UAL238", targets[0].Callsign);
            Assert.True(targets[0].ViaDataBlock);
            Assert.Equal("SWA12", targets[1].Callsign);
            Assert.False(targets[1].ViaDataBlock);
            var raised = new List<(string Callsign, Point Screen)>();
            view.Canvas.AircraftRightClicked += (callsign, screen) => raised.Add((callsign, screen));

            Click(window, view.Canvas, blockPoint, MouseButton.Right, RawInputModifiers.None);
            ContextMenu picker = Assert.IsType<ContextMenu>(view.Canvas.ActiveRightClickPicker);
            MenuItem chosen = Assert.Single(picker.Items.OfType<MenuItem>(), item => HeaderCallsign(item) == "SWA12");
            chosen.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal([("SWA12", blockPoint)], raised);
        }
        finally
        {
            Close(window);
        }
    }

    private static string HeaderCallsign(MenuItem item) => ((string)item.Header!).Split(' ')[0];

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScope()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        var view = new ScopeView { DataContext = scope };
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = view,
        };
        window.Closed += (_, _) => scope.Dispose();
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        return (main, scope, view, window);
    }

    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, double screenX, double screenY)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)screenX, (float)screenY);
        var ac = new AircraftModel
        {
            Callsign = callsign,
            AircraftType = "B738",
            FiledAircraftType = "B738",
            FlightRules = "IFR",
            TransponderMode = "C",
            Altitude = 5000,
            GroundSpeed = 250,
            Position = new LatLon(lat, lon),
        };
        main.Aircraft.Add(ac);
        Dispatcher.UIThread.RunJobs();
        return ac;
    }

    private static Point InWindow(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint, MouseButton button, RawInputModifiers modifiers)
    {
        Point at = InWindow(window, canvas, canvasPoint);
        window.MouseDown(at, button, modifiers);
        window.MouseUp(at, button, modifiers);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        FlightPlanEditorManager.Close();
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
