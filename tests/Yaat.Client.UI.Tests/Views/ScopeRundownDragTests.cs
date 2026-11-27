using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using SkiaSharp;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.UI.Tests.Fakes;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Scope;
using Yaat.Client.Views.Scope.Prefs;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

/// <summary>
/// The RUNDOWN list's drag on the Radar view: a left drag on any of its lines moves the list by the pointer's travel, kept on
/// the canvas, without touching the selection; a click that does not move leaves it where it is.
/// </summary>
public class ScopeRundownDragTests
{
    [AvaloniaFact]
    public void DraggingTheTitle_MovesTheList()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Point grab = CentreOfLine(view.Canvas, 0);

            Drag(window, view.Canvas, grab, new Point(grab.X + 120, grab.Y + 60));

            Assert.Equal(128, scope.RundownOrigin.X, 3);
            Assert.Equal(250, scope.RundownOrigin.Y, 3);
            Assert.Equal(128f, view.Canvas.CurrentFrame.Rundown.Lines[0].Rect.Left, 3f);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void DraggingARow_MovesTheList()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Point grab = CentreOfLine(view.Canvas, 1);

            Drag(window, view.Canvas, grab, new Point(grab.X + 50, grab.Y + 30));

            Assert.Equal(58, scope.RundownOrigin.X, 3);
            Assert.Equal(220, scope.RundownOrigin.Y, 3);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void RundownDrag_EndsWhenThePointerCaptureIsLost()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            IPointer? pointer = null;
            view.Canvas.AddHandler(
                InputElement.PointerPressedEvent,
                (_, e) => pointer = e.Pointer,
                RoutingStrategies.Tunnel | RoutingStrategies.Bubble,
                handledEventsToo: true
            );
            Point grab = CentreOfLine(view.Canvas, 0);
            window.MouseDown(InWindow(view.Canvas, window, grab), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.NotNull(pointer);

            pointer.Capture(null);
            window.MouseMove(InWindow(view.Canvas, window, new Point(grab.X + 120, grab.Y + 60)), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(InWindow(view.Canvas, window, new Point(grab.X + 120, grab.Y + 60)), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(ScopeViewModel.DefaultRundownOrigin, scope.RundownOrigin);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ARightReleaseDuringTheDrag_DoesNotEndIt()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Point grab = CentreOfLine(view.Canvas, 0);
            window.MouseDown(InWindow(view.Canvas, window, grab), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Point mid = InWindow(view.Canvas, window, new Point(grab.X + 10, grab.Y + 10));
            window.MouseDown(mid, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            window.MouseUp(mid, MouseButton.Right, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();

            Point to = InWindow(view.Canvas, window, new Point(grab.X + 120, grab.Y + 60));
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
            window.MouseUp(to, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(128, scope.RundownOrigin.X, 3);
            Assert.Equal(250, scope.RundownOrigin.Y, 3);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void AnOriginPastTheCanvas_DrawsInsideIt()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Rect bounds = view.Canvas.Bounds;

            scope.RundownOrigin = new Point(5000, 5000);
            RenderFrame(view.Canvas);

            SKRect title = view.Canvas.CurrentFrame.Rundown.Lines[0].Rect;
            Assert.Equal((float)(bounds.Width - 40), title.Left, 3f);
            Assert.Equal((float)(bounds.Height - 20), title.Top, 3f);
            Assert.Equal(new Point(5000, 5000), scope.RundownOrigin);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void APressOnTheList_DoesNotChangeTheSelection()
    {
        (MainViewModel main, _, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            AircraftModel ac = AddAircraft(main, view.Canvas, "UAL238", new Point(400, 400));
            main.SelectedAircraft = ac;
            RenderFrame(view.Canvas);

            Click(window, view.Canvas, CentreOfLine(view.Canvas, 1));

            Assert.Same(ac, main.SelectedAircraft);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void DraggingPastTheEdge_ClampsInsideTheCanvas()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Rect bounds = view.Canvas.Bounds;

            Drag(window, view.Canvas, CentreOfLine(view.Canvas, 0), new Point(1, 1));

            Assert.Equal(new Point(0, 0), scope.RundownOrigin);

            Drag(window, view.Canvas, CentreOfLine(view.Canvas, 0), new Point(bounds.Width - 1, bounds.Height - 1));

            Assert.Equal(new Point(bounds.Width - 40, bounds.Height - 20), scope.RundownOrigin);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void ClickWithoutMoving_DoesNotMoveTheList()
    {
        (_, ScopeViewModel scope, ScopeView view, Window window) = ShowScopeWithRows();
        try
        {
            Click(window, view.Canvas, CentreOfLine(view.Canvas, 0));

            Assert.Equal(ScopeViewModel.DefaultRundownOrigin, scope.RundownOrigin);
            Assert.Equal(new SKPoint(8, 190), view.Canvas.CurrentFrame.Rundown.Lines[0].Rect.Location);
        }
        finally
        {
            Close(window);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────

    private static (MainViewModel Main, ScopeViewModel Scope, ScopeView View, Window Window) ShowScopeWithRows()
    {
        var main = new MainViewModel(new FakeFilePickerService());
        var scope = new ScopeViewModel(main, prefStore: null, profileStore: null, ScopePrefSetStore.PrimarySlot);
        main.OpenScope = scope;
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

        main.ApplyCoordinationLists([Pfat()]);
        RenderFrame(view.Canvas);
        Assert.Equal(3, view.Canvas.CurrentFrame.Rundown.Lines.Count);
        return (main, scope, view, window);
    }

    private static CoordinationListHubDto Pfat() =>
        new(
            ListId: "PFAT",
            Title: "Oakland departure releases",
            AutoAcknowledge: false,
            SenderTcps: ["3O"],
            ReceiverTcps: ["4O"],
            Items:
            [
                new CoordinationItemHubDto("POAK-1", "SWA123", "Unsent", 7, "B738", "0425", "F", "SJC", "RWY 30", "3O", null),
                new CoordinationItemHubDto("POAK-2", "SWA456", "Unsent", 8, "B738", "0426", "F", "SJC", "RWY 30", "3O", null),
            ]
        );

    private static AircraftModel AddAircraft(MainViewModel main, ScopeCanvas canvas, string callsign, Point symbol)
    {
        (double lat, double lon) = canvas.Viewport.ScreenToLatLon((float)symbol.X, (float)symbol.Y);
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

    private static Point CentreOfLine(ScopeCanvas canvas, int index)
    {
        SKRect rect = canvas.CurrentFrame.Rundown.Lines[index].Rect;
        return new Point(rect.MidX, rect.MidY);
    }

    private static void Drag(Window window, ScopeCanvas canvas, Point fromCanvas, Point toCanvas)
    {
        Point from = InWindow(canvas, window, fromCanvas);
        Point to = InWindow(canvas, window, toCanvas);
        window.MouseDown(from, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        window.MouseMove(to, RawInputModifiers.LeftMouseButton);
        Dispatcher.UIThread.RunJobs();
        window.MouseUp(to, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static void Click(Window window, ScopeCanvas canvas, Point canvasPoint)
    {
        Point inWindow = InWindow(canvas, window, canvasPoint);
        window.MouseDown(inWindow, MouseButton.Left);
        window.MouseUp(inWindow, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point InWindow(Control canvas, Window window, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }

    // Headless frames are drawn only on a render-timer tick.
    private static void RenderFrame(ScopeCanvas canvas)
    {
        canvas.MarkDirty();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }
}
