using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Xunit;
using Yaat.Client.Models;
using Yaat.Client.Services;
using Yaat.Client.ViewModels;
using Yaat.Client.Views.Ground;
using Yaat.Sim;

namespace Yaat.Client.UI.Tests.Views;

// Shift+left-click on an aircraft or its datablock toggles that datablock hidden or shown in the Ground View, without
// selecting the aircraft or dragging its block. "Show all datablocks" shows every hidden block of the view. The Scope's
// gestures are pinned by ScopeDataBlockTests.
public class DataBlockHideGestureTests
{
    private const string Callsign = "UAL238";
    private const double FieldLat = 37.62;
    private const double FieldLon = -122.39;

    // ── Ground ───────────────────────────────────────────────────

    [AvaloniaFact]
    public void Ground_ShiftClickOnTheSymbol_HidesTheDataBlock_AndAgainShowsIt()
    {
        AircraftModel ac = MakeGroundAircraft();
        (GroundCanvas canvas, Window window) = ShowGroundCanvas(ac);
        try
        {
            var selected = new List<string>();
            canvas.AircraftLeftClicked += selected.Add;
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);
            var symbol = new Point(sx, sy);

            ShiftClick(window, canvas, symbol);
            Assert.True(canvas.IsDataBlockHidden(Callsign));

            ShiftClick(window, canvas, symbol);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
            Assert.Empty(selected);
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void Ground_ShiftClickOnTheDataBlock_HidesIt_WithoutSelectingOrDragging()
    {
        AircraftModel ac = MakeGroundAircraft();
        (GroundCanvas canvas, Window window) = ShowGroundCanvas(ac);
        try
        {
            var selected = new List<string>();
            canvas.AircraftLeftClicked += selected.Add;
            Point onBlock = GroundDataBlockPointOf(canvas);

            Point inWindow = ToWindow(canvas, window, onBlock);
            window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift);
            window.MouseMove(new Point(inWindow.X + 40, inWindow.Y + 40), RawInputModifiers.Shift | RawInputModifiers.LeftMouseButton);
            window.MouseUp(new Point(inWindow.X + 40, inWindow.Y + 40), MouseButton.Left, RawInputModifiers.Shift);
            Dispatcher.UIThread.RunJobs();

            Assert.True(canvas.IsDataBlockHidden(Callsign));
            Assert.Empty(selected);
            Assert.False(canvas.HasManualDataBlockOffset(Callsign));

            // The hidden block's area still catches the gesture, which shows it again.
            ShiftClick(window, canvas, onBlock);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaFact]
    public void Ground_ActionShiftClickOnTheSymbol_OpensTheFlightPlanEditor_AndDoesNotHide()
    {
        AircraftModel ac = MakeGroundAircraft();
        (GroundCanvas canvas, Window window) = ShowGroundCanvas(ac);
        try
        {
            var ctrlClicked = new List<string>();
            canvas.AircraftCtrlClicked += ctrlClicked.Add;
            (float sx, float sy) = canvas.Viewport.LatLonToScreen(ac.Position.Lat, ac.Position.Lon);

            ActionShiftClick(window, canvas, new Point(sx, sy));

            Assert.Equal([Callsign], ctrlClicked);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
        }
        finally
        {
            Close(window);
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void Ground_ShowAllDataBlocks_ShowsEveryBlock(bool startWithAllHidden)
    {
        AircraftModel ac = MakeGroundAircraft();
        AircraftModel other = MakeGroundAircraft();
        other.Callsign = "DAL1";
        (GroundCanvas canvas, Window window) = ShowGroundCanvas(ac);
        try
        {
            canvas.Aircraft = [ac, other];
            canvas.SetStartWithAllHidden(startWithAllHidden);
            if (!startWithAllHidden)
            {
                canvas.ToggleHiddenDataBlock(Callsign);
            }

            Assert.True(canvas.AnyDataBlockHidden);

            canvas.ShowAllDataBlocks();

            Assert.False(canvas.AnyDataBlockHidden);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
            Assert.False(canvas.IsDataBlockHidden("DAL1"));
        }
        finally
        {
            Close(window);
        }
    }

    // Push-route drawing keeps Shift+left for its marked points, even over a datablock: the press places a point and the
    // block stays shown.
    [AvaloniaFact]
    public void Ground_ShiftClickWhileDrawingAPushRoute_PlacesAMarkedPoint_AndDoesNotHide()
    {
        AircraftModel ac = MakeGroundAircraft();
        (GroundCanvas canvas, Window window) = ShowGroundCanvas(ac);
        try
        {
            Point onBlock = GroundDataBlockPointOf(canvas);
            (double startLat, double startLon) = canvas.Viewport.ScreenToLatLon(20, 20);
            (double endLat, double endLon) = canvas.Viewport.ScreenToLatLon(700, 500);
            canvas.Layout = new GroundLayoutDto(
                "SFO",
                [
                    new GroundNodeDto(1, startLat, startLon, "TaxiwayIntersection", null, null, null),
                    new GroundNodeDto(2, endLat, endLon, "TaxiwayIntersection", null, null, null),
                ],
                [],
                null,
                null,
                null,
                IsRunwaysOnly: false
            );
            canvas.IsDrawingRoute = true;
            canvas.DrawWaypoints = [1, 2];
            canvas.PushWaypointMarks =
            [
                new PushWaypointMark(new LatLon(startLat, startLon), null, null),
                new PushWaypointMark(new LatLon(endLat, endLon), null, null),
            ];
            RenderFrame();
            onBlock = GroundDataBlockPointOf(canvas);
            var placed = new List<LatLon>();
            canvas.DrawFreePointPlaced += (point, _, _) => placed.Add(point);

            ShiftClick(window, canvas, onBlock);

            Assert.Single(placed);
            Assert.False(canvas.IsDataBlockHidden(Callsign));
        }
        finally
        {
            Close(window);
        }
    }

    // ── Helpers ──────────────────────────────────────────────────

    private static (GroundCanvas Canvas, Window Window) ShowGroundCanvas(AircraftModel ac)
    {
        var canvas = new GroundCanvas();
        canvas.Viewport.CenterLat = FieldLat;
        canvas.Viewport.CenterLon = FieldLon;
        canvas.Viewport.Zoom = 1.0;
        canvas.AirportCenterLat = FieldLat;
        canvas.AirportCenterLon = FieldLon;
        canvas.AirportElevation = 0;
        canvas.Aircraft = [ac];
        Window window = ShowInWindow(canvas);
        RenderFrame();
        return (canvas, window);
    }

    private static Window ShowInWindow(Control content)
    {
        var window = new Window
        {
            Width = 800,
            Height = 600,
            Content = content,
        };
        window.Show();
        for (int i = 0; i < 5; i++)
        {
            Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
        }

        RenderFrame();
        return window;
    }

    private static void Close(Window window)
    {
        window.Close();
        Dispatcher.UIThread.RunJobs();
    }

    // Headless frames are drawn only on a render-timer tick.
    private static void RenderFrame()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static AircraftModel MakeGroundAircraft() =>
        new()
        {
            Callsign = Callsign,
            AircraftType = "B738",
            Destination = "KLAX",
            FlightRules = "IFR",
            TransponderMode = "C",
            IsOnGround = true,
            Altitude = 0,
            Position = new LatLon(FieldLat, FieldLon),
        };

    private static Point GroundDataBlockPointOf(GroundCanvas canvas)
    {
        AircraftModel aircraft = canvas.Aircraft![0];
        (float x, float y) = canvas.Viewport.LatLonToScreen(aircraft.Position.Lat, aircraft.Position.Lon);
        for (int dy = -80; dy <= 40; dy += 2)
        {
            for (int dx = 12; dx <= 160; dx += 2)
            {
                var point = new Point(x + dx, y + dy);
                if ((canvas.FindDataBlockAtPoint(point) == aircraft) && (canvas.FindAircraftAtPoint(point) is null))
                {
                    return point;
                }
            }
        }

        Assert.Fail($"no point on {aircraft.Callsign}'s datablock near its symbol");
        return default;
    }

    private static void ShiftClick(Window window, Control canvas, Point canvasPoint)
    {
        Point inWindow = ToWindow(canvas, window, canvasPoint);
        window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift);
        window.MouseUp(inWindow, MouseButton.Left, RawInputModifiers.Shift);
        Dispatcher.UIThread.RunJobs();
    }

    // Shift plus the platform's action modifier (Cmd on macOS, Ctrl elsewhere), as PlatformHelper.HasActionModifier reads it.
    private static void ActionShiftClick(Window window, Control canvas, Point canvasPoint)
    {
        RawInputModifiers action = PlatformHelper.IsMacOS ? RawInputModifiers.Meta : RawInputModifiers.Control;
        Point inWindow = ToWindow(canvas, window, canvasPoint);
        window.MouseDown(inWindow, MouseButton.Left, RawInputModifiers.Shift | action);
        window.MouseUp(inWindow, MouseButton.Left, RawInputModifiers.Shift | action);
        Dispatcher.UIThread.RunJobs();
    }

    private static Point ToWindow(Control canvas, Window window, Point canvasPoint)
    {
        Point? inWindow = canvas.TranslatePoint(canvasPoint, window);
        Assert.NotNull(inWindow);
        return inWindow.Value;
    }
}
